using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Typeless
{
    // GLB (binary glTF 2.0). Read: default scene, node tree (matrix or TRS) baked in double together
    // with the meters Y-up to millimeters Z-up mapping, triangles/strips/fans, baseColorFactor.
    // Write: raw mm Z-up float32 vertices under one root node whose matrix maps them back to meters
    // Y-up, so the vertex buffers stay bit exact. Use through ModelBackend.Read / Write.
    internal static class Glb
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private const uint Magic = 0x46546C67;      // "glTF"
        private const uint ChunkJson = 0x4E4F534A;  // "JSON"
        private const uint ChunkBin = 0x004E4942;   // "BIN\0"

        private const string Compressed = "Compressed glb (Draco/meshopt) is not supported";

        // Column-major 4x4 matrices (m[col * 4 + row]), column vectors: p' = M p.
        // glTF (x, y, z) meters Y-up to (1000x, -1000z, 1000y) millimeters Z-up.
        private static readonly double[] ToMm =
        {
            1000, 0, 0, 0,
            0, 0, 1000, 0,
            0, -1000, 0, 0,
            0, 0, 0, 1,
        };

        // The writer's root node matrix, the inverse of ToMm: x' = 0.001x, y' = 0.001z, z' = -0.001y.
        private static readonly double[] ToMeters =
        {
            0.001, 0, 0, 0,
            0, 0, -0.001, 0,
            0, 0.001, 0, 0,
            0, 0, 0, 1,
        };

        private sealed class Reader
        {
            public byte[] Data = Array.Empty<byte>();
            public int BinOffset = -1, BinLength;
            public JsonElement[] Nodes = Array.Empty<JsonElement>();
            public JsonElement[] Meshes = Array.Empty<JsonElement>();
            public JsonElement[] Accessors = Array.Empty<JsonElement>();
            public JsonElement[] Views = Array.Empty<JsonElement>();
            public JsonElement[] Materials = Array.Empty<JsonElement>();
            public JsonElement[] Buffers = Array.Empty<JsonElement>();
            public readonly Dictionary<int, (byte[] Bytes, int Offset, int Length)> BufferData =
                new Dictionary<int, (byte[], int, int)>();
            public readonly Scene Scene = new Scene();
            public string FileName = "";
        }

        public static Scene Read(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 12 || ReadU32(data, 0) != Magic)
                throw new InvalidOperationException("Not a valid glb file: no glTF header");
            if (ReadU32(data, 4) != 2) throw new InvalidOperationException("Only glTF 2.0 .glb files are supported");
            try
            {
                return ReadContainer(data, Path.GetFileName(path));
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException("Not a valid glb file: " + ex.Message, ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Not a valid glb file: bad JSON (" + ex.Message + ")", ex);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Not a valid glb file: bad data (" + ex.Message + ")", ex);
            }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException("Not a valid glb file: number out of range (" + ex.Message + ")", ex);
            }
        }

        private static Scene ReadContainer(byte[] data, string fileName)
        {
            long declared = ReadU32(data, 8);
            if (declared > data.Length) throw new InvalidDataException("the file is truncated");
            if (declared < data.Length)
                Log.Write($"GLB: {fileName}: {data.Length - declared} bytes after the declared length; ignored them");
            int end = (int)declared;

            int jsonOffset = -1, jsonLength = 0;
            var rd = new Reader { Data = data, FileName = fileName };
            long o = 12;
            bool first = true;
            while (o + 8 <= end)
            {
                long len = ReadU32(data, (int)o);
                uint type = ReadU32(data, (int)o + 4);
                long start = o + 8;
                if (start + len > end) throw new InvalidDataException("a chunk runs past the end of the file");
                if (first)
                {
                    if (type != ChunkJson) throw new InvalidDataException("the first chunk is not JSON");
                    jsonOffset = (int)start;
                    jsonLength = (int)len;
                    first = false;
                }
                else if (type == ChunkBin && rd.BinOffset < 0)
                {
                    rd.BinOffset = (int)start;
                    rd.BinLength = (int)len;
                }
                o = start + len;
            }
            if (jsonOffset < 0) throw new InvalidDataException("no JSON chunk");

            // Some writers pad the JSON with zeros instead of spaces, or start it with a BOM.
            while (jsonLength > 0 && (data[jsonOffset + jsonLength - 1] == 0 || data[jsonOffset + jsonLength - 1] == 0x20))
                jsonLength--;
            if (jsonLength >= 3 && data[jsonOffset] == 0xEF && data[jsonOffset + 1] == 0xBB && data[jsonOffset + 2] == 0xBF)
            {
                jsonOffset += 3;
                jsonLength -= 3;
            }

            using JsonDocument doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, jsonOffset, jsonLength));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the JSON is not an object");

            CheckExtensions(root);
            NoteRootDrops(rd, root);

            rd.Nodes = ArrayOf(root, "nodes");
            rd.Meshes = ArrayOf(root, "meshes");
            rd.Accessors = ArrayOf(root, "accessors");
            rd.Views = ArrayOf(root, "bufferViews");
            rd.Materials = ArrayOf(root, "materials");
            rd.Buffers = ArrayOf(root, "buffers");

            var path = new HashSet<int>();
            foreach (int n in SceneRoots(rd, root))
                Visit(rd, n, ToMm, path);
            return rd.Scene;
        }

        private static void CheckExtensions(JsonElement root)
        {
            foreach (string key in new[] { "extensionsUsed", "extensionsRequired" })
            {
                foreach (JsonElement e in ArrayOf(root, key))
                {
                    if (e.ValueKind != JsonValueKind.String) continue;
                    string? ext = e.GetString();
                    if (ext == "KHR_draco_mesh_compression" || ext == "EXT_meshopt_compression" || ext == "KHR_meshopt_compression")
                        throw new InvalidOperationException(Compressed);
                }
            }
        }

        private static void NoteRootDrops(Reader rd, JsonElement root)
        {
            Scene s = rd.Scene;
            if (ArrayOf(root, "images").Length > 0 || ArrayOf(root, "textures").Length > 0) s.Drop("textures");
            if (ArrayOf(root, "animations").Length > 0 || ArrayOf(root, "skins").Length > 0) s.Drop("skins and animations");
            if (ArrayOf(root, "cameras").Length > 0) s.Drop("cameras and lights");
            foreach (JsonElement e in ArrayOf(root, "extensionsUsed"))
                if (e.ValueKind == JsonValueKind.String && e.GetString() == "KHR_lights_punctual") s.Drop("cameras and lights");
        }

        // The default scene: "scene", else scenes[0], else every node that is nobody's child.
        private static List<int> SceneRoots(Reader rd, JsonElement root)
        {
            var roots = new List<int>();
            JsonElement[] scenes = ArrayOf(root, "scenes");
            int si = -1;
            if (Prop(root, "scene") is JsonElement sv) si = ToInt(sv, "scene");
            else if (scenes.Length > 0) si = 0;

            if (si >= 0)
            {
                JsonElement scene = Item(scenes, si, "scene");
                foreach (JsonElement n in ArrayOf(scene, "nodes")) roots.Add(ToInt(n, "scene node"));
                return roots;
            }

            var isChild = new bool[rd.Nodes.Length];
            foreach (JsonElement node in rd.Nodes)
            {
                foreach (JsonElement c in ArrayOf(node, "children"))
                {
                    int ci = ToInt(c, "child");
                    if (ci >= 0 && ci < isChild.Length) isChild[ci] = true;
                }
            }
            for (int i = 0; i < isChild.Length; i++)
                if (!isChild[i]) roots.Add(i);
            return roots;
        }

        // Depth first; parent maps this node's parent space to world millimeters.
        private static void Visit(Reader rd, int index, double[] parent, HashSet<int> path)
        {
            if (!path.Add(index)) throw new InvalidDataException($"node {index} is its own ancestor");
            JsonElement node = Item(rd.Nodes, index, "node");
            double[] world = Mul(parent, Local(node));

            if (Prop(node, "skin") != null) rd.Scene.Drop("skins and animations");
            if (Prop(node, "camera") != null) rd.Scene.Drop("cameras and lights");
            if (Prop(node, "weights") != null) rd.Scene.Drop("morph targets");
            if (Prop(node, "extensions") is JsonElement ext && ext.ValueKind == JsonValueKind.Object)
            {
                if (ext.TryGetProperty("EXT_mesh_gpu_instancing", out _)) rd.Scene.Drop("glb gpu instancing (base mesh kept)");
                if (ext.TryGetProperty("KHR_lights_punctual", out _)) rd.Scene.Drop("cameras and lights");
            }

            if (Prop(node, "mesh") is JsonElement mv)
                EmitMesh(rd, node, ToInt(mv, "mesh"), world);

            foreach (JsonElement c in ArrayOf(node, "children"))
                Visit(rd, ToInt(c, "child"), world, path);
            path.Remove(index);
        }

        private static void EmitMesh(Reader rd, JsonElement node, int meshIndex, double[] world)
        {
            JsonElement mesh = Item(rd.Meshes, meshIndex, "mesh");
            string baseName = Str(node, "name") ?? Str(mesh, "name") ?? $"mesh {meshIndex}";
            if (Prop(mesh, "weights") != null) rd.Scene.Drop("morph targets");

            JsonElement[] prims = ArrayOf(mesh, "primitives");
            for (int k = 0; k < prims.Length; k++)
            {
                JsonElement prim = prims[k];
                string name = k == 0 ? baseName : $"{baseName} {k + 1}";
                if (Prop(prim, "extensions") is JsonElement pext && pext.ValueKind == JsonValueKind.Object
                    && pext.TryGetProperty("KHR_draco_mesh_compression", out _))
                    throw new InvalidOperationException(Compressed);

                int mode = Prop(prim, "mode") is JsonElement modeV ? ToInt(modeV, "mode") : 4;
                if (mode >= 0 && mode <= 3)
                {
                    rd.Scene.Drop("glb points and lines");
                    continue;
                }
                if (mode != 4 && mode != 5 && mode != 6)
                {
                    Log.Write($"GLB: {rd.FileName}: skipped a primitive of {baseName} with unknown mode {mode}");
                    continue;
                }

                if (ArrayOf(prim, "targets").Length > 0) rd.Scene.Drop("morph targets");
                if (Prop(prim, "attributes") is not JsonElement attrs || attrs.ValueKind != JsonValueKind.Object) continue;
                int position = -1;
                foreach (JsonProperty a in attrs.EnumerateObject())
                {
                    string an = a.Name;
                    if (an == "POSITION") position = ToInt(a.Value, "POSITION");
                    else if (an.StartsWith("TEXCOORD_", StringComparison.Ordinal)) rd.Scene.Drop("uvs");
                    else if (an == "NORMAL") rd.Scene.Drop("normals");
                    else if (an == "TANGENT") rd.Scene.Drop("tangents");
                    else if (an.StartsWith("COLOR_", StringComparison.Ordinal)) rd.Scene.Drop("vertex colors");
                    else if (an.StartsWith("JOINTS_", StringComparison.Ordinal) || an.StartsWith("WEIGHTS_", StringComparison.Ordinal))
                        rd.Scene.Drop("skins and animations"); // bind pose: positions as stored
                }
                if (position < 0) continue;

                float[] pos = ReadPositions(rd, position, world);
                int vertices = pos.Length / 3;
                int[] idx = Prop(prim, "indices") is JsonElement iv ? ReadIndices(rd, ToInt(iv, "indices")) : Sequential(vertices);
                int[] tris = mode switch
                {
                    5 => Strip(idx),
                    6 => Fan(idx),
                    _ => Whole(rd, idx, baseName),
                };
                Rgba? color = Prop(prim, "material") is JsonElement mat ? ColorOf(rd, ToInt(mat, "material")) : null;
                Compact(ref pos, tris);
                rd.Scene.AddPart(name, pos, tris, color);
            }
        }

        // Accessor of float VEC3, honoring bufferView and accessor offsets and byteStride, baked by m.
        private static float[] ReadPositions(Reader rd, int accessorIndex, double[] m)
        {
            JsonElement acc = Item(rd.Accessors, accessorIndex, "accessor");
            if (Prop(acc, "sparse") != null) throw new InvalidOperationException("Sparse accessors are not supported");
            if (Str(acc, "type") != "VEC3") throw new InvalidDataException($"POSITION accessor {accessorIndex} is not VEC3");
            int componentType = ToInt(Prop(acc, "componentType") ?? throw new InvalidDataException("accessor without componentType"), "componentType");
            if (componentType != 5126) throw new InvalidOperationException("Quantized glb is not supported");
            int count = ToInt(Prop(acc, "count") ?? throw new InvalidDataException("accessor without count"), "count");
            if (count < 0) throw new InvalidDataException("negative accessor count");

            var raw = new float[(long)count * 3];
            if (Prop(acc, "bufferView") is JsonElement bv)
            {
                (byte[] bytes, int viewStart, int viewLength, int? viewStride) = View(rd, ToInt(bv, "bufferView"));
                long start = viewStart + (long)OptInt(acc, "byteOffset", 0);
                int stride = viewStride ?? 12;
                if (stride < 12) throw new InvalidDataException($"byteStride {stride} is smaller than a VEC3 float");
                if (count > 0 && start + (long)(count - 1) * stride + 12 > (long)viewStart + viewLength)
                    throw new InvalidDataException($"accessor {accessorIndex} runs past its bufferView");
                ReadOnlySpan<byte> span = bytes;
                for (int v = 0; v < count; v++)
                {
                    int p = (int)(start + (long)v * stride);
                    raw[v * 3] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p, 4));
                    raw[v * 3 + 1] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p + 4, 4));
                    raw[v * 3 + 2] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(p + 8, 4));
                }
            }
            // No bufferView: all zeros (glTF spec), the transform still applies.
            return Bake(raw, m);
        }

        private static int[] ReadIndices(Reader rd, int accessorIndex)
        {
            JsonElement acc = Item(rd.Accessors, accessorIndex, "accessor");
            if (Prop(acc, "sparse") != null) throw new InvalidOperationException("Sparse accessors are not supported");
            int componentType = ToInt(Prop(acc, "componentType") ?? throw new InvalidDataException("accessor without componentType"), "componentType");
            int size = componentType switch
            {
                5121 => 1,
                5123 => 2,
                5125 => 4,
                _ => throw new InvalidDataException($"index componentType {componentType} is not ubyte, ushort or uint"),
            };
            int count = ToInt(Prop(acc, "count") ?? throw new InvalidDataException("accessor without count"), "count");
            if (count < 0) throw new InvalidDataException("negative accessor count");
            var idx = new int[count];
            if (Prop(acc, "bufferView") is not JsonElement bv) return idx;

            (byte[] bytes, int viewStart, int viewLength, int? viewStride) = View(rd, ToInt(bv, "bufferView"));
            long start = viewStart + (long)OptInt(acc, "byteOffset", 0);
            int stride = viewStride ?? size;
            if (stride < size) throw new InvalidDataException("index byteStride is smaller than the index size");
            if (count > 0 && start + (long)(count - 1) * stride + size > (long)viewStart + viewLength)
                throw new InvalidDataException($"accessor {accessorIndex} runs past its bufferView");
            ReadOnlySpan<byte> span = bytes;
            for (int i = 0; i < count; i++)
            {
                int p = (int)(start + (long)i * stride);
                uint v = size switch
                {
                    1 => span[p],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(p, 2)),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(p, 4)),
                };
                if (v > int.MaxValue) throw new InvalidDataException($"index {v} out of range");
                idx[i] = (int)v;
            }
            return idx;
        }

        // (bytes, start, length, byteStride) of a bufferView, inside its buffer.
        private static (byte[] Bytes, int Start, int Length, int? Stride) View(Reader rd, int viewIndex)
        {
            JsonElement view = Item(rd.Views, viewIndex, "bufferView");
            if (Prop(view, "extensions") is JsonElement ext && ext.ValueKind == JsonValueKind.Object
                && (ext.TryGetProperty("EXT_meshopt_compression", out _) || ext.TryGetProperty("KHR_meshopt_compression", out _)))
                throw new InvalidOperationException(Compressed);
            int buffer = ToInt(Prop(view, "buffer") ?? throw new InvalidDataException("bufferView without buffer"), "buffer");
            int offset = OptInt(view, "byteOffset", 0);
            int length = ToInt(Prop(view, "byteLength") ?? throw new InvalidDataException("bufferView without byteLength"), "byteLength");
            int? stride = Prop(view, "byteStride") is JsonElement s ? ToInt(s, "byteStride") : null;
            (byte[] bytes, int bufStart, int bufLength) = Buffer(rd, buffer);
            if (offset < 0 || length < 0 || (long)offset + length > bufLength)
                throw new InvalidDataException($"bufferView {viewIndex} runs past buffer {buffer}");
            return (bytes, bufStart + offset, length, stride);
        }

        // Buffer 0 without a uri is the BIN chunk; data: base64 uris are decoded; external files are refused.
        private static (byte[] Bytes, int Start, int Length) Buffer(Reader rd, int index)
        {
            if (rd.BufferData.TryGetValue(index, out var cached)) return cached;
            JsonElement def = Item(rd.Buffers, index, "buffer");
            int declared = OptInt(def, "byteLength", -1);
            (byte[], int, int) result;
            string? uri = Str(def, "uri");
            if (uri != null)
            {
                if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("External buffers are not supported");
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"buffer {index} has a data uri that is not base64");
                byte[] bytes = Convert.FromBase64String(uri.Substring(comma + 1));
                int len = declared >= 0 ? declared : bytes.Length;
                if (len > bytes.Length) throw new InvalidDataException($"buffer {index} is shorter than its byteLength");
                result = (bytes, 0, len);
            }
            else
            {
                if (index != 0 || rd.BinOffset < 0) throw new InvalidDataException($"buffer {index} has no data");
                int len = declared >= 0 ? declared : rd.BinLength;
                if (len > rd.BinLength) throw new InvalidDataException("the BIN chunk is shorter than buffer 0");
                result = (rd.Data, rd.BinOffset, len);
            }
            rd.BufferData[index] = result;
            return result;
        }

        // baseColorFactor of the material (default opaque white); textures are noted as dropped.
        private static Rgba? ColorOf(Reader rd, int materialIndex)
        {
            JsonElement mat = Item(rd.Materials, materialIndex, "material");
            double[] f = { 1, 1, 1, 1 };
            if (Prop(mat, "pbrMetallicRoughness") is JsonElement pbr && pbr.ValueKind == JsonValueKind.Object)
            {
                JsonElement[] bc = ArrayOf(pbr, "baseColorFactor");
                if (bc.Length == 4)
                    for (int i = 0; i < 4; i++) f[i] = ToNum(bc[i], "baseColorFactor");
                if (Prop(pbr, "baseColorTexture") != null || Prop(pbr, "metallicRoughnessTexture") != null)
                    rd.Scene.Drop("textures");
            }
            if (Prop(mat, "normalTexture") != null || Prop(mat, "occlusionTexture") != null || Prop(mat, "emissiveTexture") != null)
                rd.Scene.Drop("textures");
            return Rgba.FromUnit(f[0], f[1], f[2], f[3]);
        }

        // Primitives often share one big POSITION accessor. Keep only the vertices this primitive's
        // triangles use (original order, no welding) and renumber tris in place.
        private static void Compact(ref float[] pos, int[] tris)
        {
            int vertices = pos.Length / 3;
            var map = new int[vertices];
            foreach (int v in tris)
            {
                if (v < 0 || v >= vertices) throw new InvalidDataException($"vertex index {v} out of range (0..{vertices - 1})");
                map[v] = 1;
            }
            int used = 0;
            for (int v = 0; v < vertices; v++) map[v] = map[v] != 0 ? used++ : -1;
            if (used == vertices) return;

            var dst = new float[used * 3];
            for (int v = 0; v < vertices; v++)
            {
                int n = map[v];
                if (n < 0) continue;
                dst[n * 3] = pos[v * 3];
                dst[n * 3 + 1] = pos[v * 3 + 1];
                dst[n * 3 + 2] = pos[v * 3 + 2];
            }
            for (int i = 0; i < tris.Length; i++) tris[i] = map[tris[i]];
            pos = dst;
        }

        private static int[] Sequential(int n)
        {
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            return idx;
        }

        private static int[] Whole(Reader rd, int[] idx, string name)
        {
            int extra = idx.Length % 3;
            if (extra == 0) return idx;
            Log.Write($"GLB: {rd.FileName}: {name} has {extra} trailing indices after the last triangle; ignored them");
            var t = new int[idx.Length - extra];
            Array.Copy(idx, t, t.Length);
            return t;
        }

        // Triangle i of a strip is (v_i, v_i+1, v_i+2) for even i and (v_i, v_i+2, v_i+1) for odd i,
        // so the winding stays consistent.
        private static int[] Strip(int[] idx)
        {
            int n = idx.Length - 2;
            if (n <= 0) return Array.Empty<int>();
            var t = new int[n * 3];
            for (int i = 0; i < n; i++)
            {
                bool odd = (i & 1) == 1;
                t[i * 3] = idx[i];
                t[i * 3 + 1] = odd ? idx[i + 2] : idx[i + 1];
                t[i * 3 + 2] = odd ? idx[i + 1] : idx[i + 2];
            }
            return t;
        }

        // Triangle i of a fan is (v_i+1, v_i+2, v_0), per the glTF spec.
        private static int[] Fan(int[] idx)
        {
            int n = idx.Length - 2;
            if (n <= 0) return Array.Empty<int>();
            var t = new int[n * 3];
            for (int i = 0; i < n; i++)
            {
                t[i * 3] = idx[i + 1];
                t[i * 3 + 1] = idx[i + 2];
                t[i * 3 + 2] = idx[0];
            }
            return t;
        }

        // ---- transforms ----

        // Node matrix (16 numbers, column-major) or T * R * S; identity when neither is given.
        private static double[] Local(JsonElement node)
        {
            JsonElement[] mat = ArrayOf(node, "matrix");
            if (mat.Length == 16)
            {
                var m = new double[16];
                for (int i = 0; i < 16; i++) m[i] = ToNum(mat[i], "matrix");
                return m;
            }

            double tx = 0, ty = 0, tz = 0, qx = 0, qy = 0, qz = 0, qw = 1, sx = 1, sy = 1, sz = 1;
            JsonElement[] t = ArrayOf(node, "translation");
            if (t.Length == 3) { tx = ToNum(t[0], "translation"); ty = ToNum(t[1], "translation"); tz = ToNum(t[2], "translation"); }
            JsonElement[] r = ArrayOf(node, "rotation");
            if (r.Length == 4)
            {
                qx = ToNum(r[0], "rotation"); qy = ToNum(r[1], "rotation");
                qz = ToNum(r[2], "rotation"); qw = ToNum(r[3], "rotation");
            }
            JsonElement[] s = ArrayOf(node, "scale");
            if (s.Length == 3) { sx = ToNum(s[0], "scale"); sy = ToNum(s[1], "scale"); sz = ToNum(s[2], "scale"); }

            // Rotation from the quaternion; dividing by its squared norm tolerates a slightly off unit length.
            double n2 = qx * qx + qy * qy + qz * qz + qw * qw;
            double k = n2 > 0 ? 2 / n2 : 0;
            double r00 = 1 - k * (qy * qy + qz * qz), r01 = k * (qx * qy - qz * qw), r02 = k * (qx * qz + qy * qw);
            double r10 = k * (qx * qy + qz * qw), r11 = 1 - k * (qx * qx + qz * qz), r12 = k * (qy * qz - qx * qw);
            double r20 = k * (qx * qz - qy * qw), r21 = k * (qy * qz + qx * qw), r22 = 1 - k * (qx * qx + qy * qy);

            return new[]
            {
                r00 * sx, r10 * sx, r20 * sx, 0,
                r01 * sy, r11 * sy, r21 * sy, 0,
                r02 * sz, r12 * sz, r22 * sz, 0,
                tx, ty, tz, 1,
            };
        }

        // a * b: b applies first.
        private static double[] Mul(double[] a, double[] b)
        {
            var c = new double[16];
            for (int col = 0; col < 4; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a[k * 4 + row] * b[col * 4 + k];
                    c[col * 4 + row] = s;
                }
            }
            return c;
        }

        private static bool IsIdentity(double[] m)
        {
            for (int i = 0; i < 16; i++)
                if (m[i] != ((i % 5 == 0) ? 1 : 0)) return false;
            return true;
        }

        // Positions to world millimeters in double, cast to float32 once. Identity copies the floats
        // as they are. Zero matrix entries are skipped, so a coordinate that only gets multiplied by
        // 1 keeps its exact bits (including -0).
        private static float[] Bake(float[] src, double[] m)
        {
            if (IsIdentity(m)) return src;
            var dst = new float[src.Length];
            for (int i = 0; i + 2 < src.Length; i += 3)
            {
                double x = src[i], y = src[i + 1], z = src[i + 2];
                dst[i] = Row(x, y, z, m[0], m[4], m[8], m[12]);
                dst[i + 1] = Row(x, y, z, m[1], m[5], m[9], m[13]);
                dst[i + 2] = Row(x, y, z, m[2], m[6], m[10], m[14]);
            }
            return dst;
        }

        private static float Row(double x, double y, double z, double a, double b, double c, double t)
        {
            double s = 0;
            bool any = false;
            if (a != 0) { s = x * a; any = true; }
            if (b != 0) { s = any ? s + y * b : y * b; any = true; }
            if (c != 0) { s = any ? s + z * c : z * c; any = true; }
            if (t != 0) s = any ? s + t : t;
            return (float)s;
        }

        // ---- JSON helpers ----

        private static JsonElement? Prop(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null
                ? v : null;

        // The array's elements (empty when missing or not an array), copied so indexing is O(1).
        private static JsonElement[] ArrayOf(JsonElement e, string name)
        {
            if (Prop(e, name) is not JsonElement a || a.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
            var list = new JsonElement[a.GetArrayLength()];
            int i = 0;
            foreach (JsonElement item in a.EnumerateArray()) list[i++] = item;
            return list;
        }

        private static JsonElement Item(JsonElement[] arr, int index, string what)
        {
            if (index < 0 || index >= arr.Length) throw new InvalidDataException($"{what} {index} does not exist");
            return arr[index];
        }

        private static string? Str(JsonElement e, string name)
        {
            if (Prop(e, name) is not JsonElement v || v.ValueKind != JsonValueKind.String) return null;
            string? s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static int ToInt(JsonElement v, string what)
        {
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetInt32(out int i)) return i;
                double d = v.GetDouble();
                if (d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
            }
            throw new InvalidDataException($"{what} is not an integer");
        }

        private static int OptInt(JsonElement e, string name, int def) =>
            Prop(e, name) is JsonElement v ? ToInt(v, name) : def;

        private static double ToNum(JsonElement v, string what)
        {
            if (v.ValueKind != JsonValueKind.Number) throw new InvalidDataException($"{what} holds a value that is not a number");
            return v.GetDouble();
        }

        private static uint ReadU32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

        // ---- writer ----

        // One root node "Typeless" (matrix ToMeters) with one child node and mesh per part. Positions are
        // the raw mm Z-up float32 values with exact min/max, indices uint32. One material per distinct color.
        public static void Write(Scene scene, string path)
        {
            var parts = new List<Part>();
            foreach (Part p in scene.Parts)
                if (p.TriangleCount > 0) parts.Add(p);

            var colors = new List<Rgba>();
            var colorIndex = new Dictionary<Rgba, int>();
            foreach (Part p in parts)
            {
                if (p.Color is Rgba c && !colorIndex.ContainsKey(c))
                {
                    colorIndex[c] = colors.Count;
                    colors.Add(c);
                }
            }

            // BIN layout: per part its positions, then its indices; every view a multiple of 4 bytes.
            var posOffset = new long[parts.Count];
            var idxOffset = new long[parts.Count];
            long binLength = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                posOffset[i] = binLength;
                binLength += (long)parts[i].VertexCount * 12;
                idxOffset[i] = binLength;
                binLength += (long)parts[i].Triangles.Length * 4;
            }
            if (binLength > int.MaxValue - 1024) throw new InvalidOperationException("Model too large for glb.");

            var bin = new byte[binLength];
            var mins = new float[parts.Count][];
            var maxs = new float[parts.Count][];
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                float[] pos = p.Positions;
                int n = p.VertexCount * 3;
                var min = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
                var max = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
                Span<byte> dst = bin.AsSpan((int)posOffset[i], n * 4);
                for (int j = 0; j < n; j++)
                {
                    float f = pos[j];
                    if (!float.IsFinite(f)) throw new InvalidOperationException("glb cannot store NaN or infinite coordinates.");
                    int k = j % 3;
                    if (f < min[k]) min[k] = f;
                    if (f > max[k]) max[k] = f;
                    BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(j * 4, 4), f);
                }
                mins[i] = min;
                maxs[i] = max;

                int[] tri = p.Triangles;
                Span<byte> idst = bin.AsSpan((int)idxOffset[i], tri.Length * 4);
                for (int j = 0; j < tri.Length; j++)
                    BinaryPrimitives.WriteUInt32LittleEndian(idst.Slice(j * 4, 4), (uint)tri[j]);
            }

            byte[] json;
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();

                    w.WriteStartObject("asset");
                    w.WriteString("version", "2.0");
                    w.WriteString("generator", "Typeless");
                    w.WriteEndObject();

                    w.WriteNumber("scene", 0);
                    w.WriteStartArray("scenes");
                    w.WriteStartObject();
                    w.WriteStartArray("nodes");
                    w.WriteNumberValue(0);
                    w.WriteEndArray();
                    w.WriteEndObject();
                    w.WriteEndArray();

                    w.WriteStartArray("nodes");
                    w.WriteStartObject();
                    w.WriteString("name", "Typeless");
                    w.WriteStartArray("matrix");
                    foreach (double d in ToMeters) w.WriteRawValue(D(d));
                    w.WriteEndArray();
                    if (parts.Count > 0)
                    {
                        w.WriteStartArray("children");
                        for (int i = 0; i < parts.Count; i++) w.WriteNumberValue(i + 1);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                    for (int i = 0; i < parts.Count; i++)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", PartName(parts[i]));
                        w.WriteNumber("mesh", i);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();

                    if (parts.Count > 0)
                    {
                        w.WriteStartArray("meshes");
                        for (int i = 0; i < parts.Count; i++)
                        {
                            w.WriteStartObject();
                            w.WriteString("name", PartName(parts[i]));
                            w.WriteStartArray("primitives");
                            w.WriteStartObject();
                            w.WriteStartObject("attributes");
                            w.WriteNumber("POSITION", i * 2);
                            w.WriteEndObject();
                            w.WriteNumber("indices", i * 2 + 1);
                            if (parts[i].Color is Rgba c) w.WriteNumber("material", colorIndex[c]);
                            w.WriteEndObject();
                            w.WriteEndArray();
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }

                    if (colors.Count > 0)
                    {
                        w.WriteStartArray("materials");
                        foreach (Rgba c in colors)
                        {
                            w.WriteStartObject();
                            w.WriteString("name", c.Hex);
                            w.WriteStartObject("pbrMetallicRoughness");
                            w.WriteStartArray("baseColorFactor");
                            w.WriteRawValue(D(c.R / 255.0));
                            w.WriteRawValue(D(c.G / 255.0));
                            w.WriteRawValue(D(c.B / 255.0));
                            w.WriteRawValue(D(c.A / 255.0));
                            w.WriteEndArray();
                            w.WriteNumber("metallicFactor", 0);
                            w.WriteNumber("roughnessFactor", 1);
                            w.WriteEndObject();
                            if (c.A < 255) w.WriteString("alphaMode", "BLEND");
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }

                    if (parts.Count > 0)
                    {
                        w.WriteStartArray("accessors");
                        for (int i = 0; i < parts.Count; i++)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("bufferView", i * 2);
                            w.WriteNumber("componentType", 5126);
                            w.WriteNumber("count", parts[i].VertexCount);
                            w.WriteString("type", "VEC3");
                            w.WriteStartArray("min");
                            foreach (float f in mins[i]) w.WriteRawValue(F(f));
                            w.WriteEndArray();
                            w.WriteStartArray("max");
                            foreach (float f in maxs[i]) w.WriteRawValue(F(f));
                            w.WriteEndArray();
                            w.WriteEndObject();

                            w.WriteStartObject();
                            w.WriteNumber("bufferView", i * 2 + 1);
                            w.WriteNumber("componentType", 5125);
                            w.WriteNumber("count", parts[i].Triangles.Length);
                            w.WriteString("type", "SCALAR");
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();

                        w.WriteStartArray("bufferViews");
                        for (int i = 0; i < parts.Count; i++)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("buffer", 0);
                            w.WriteNumber("byteOffset", posOffset[i]);
                            w.WriteNumber("byteLength", (long)parts[i].VertexCount * 12);
                            w.WriteNumber("target", 34962);
                            w.WriteEndObject();

                            w.WriteStartObject();
                            w.WriteNumber("buffer", 0);
                            w.WriteNumber("byteOffset", idxOffset[i]);
                            w.WriteNumber("byteLength", (long)parts[i].Triangles.Length * 4);
                            w.WriteNumber("target", 34963);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();

                        w.WriteStartArray("buffers");
                        w.WriteStartObject();
                        w.WriteNumber("byteLength", binLength);
                        w.WriteEndObject();
                        w.WriteEndArray();
                    }

                    w.WriteEndObject();
                }
                json = ms.ToArray();
            }

            int jsonPad = (4 - json.Length % 4) % 4;
            int binPad = (int)((4 - binLength % 4) % 4);
            long total = 12 + 8 + json.Length + jsonPad + (binLength > 0 ? 8 + binLength + binPad : 0);
            if (total > uint.MaxValue) throw new InvalidOperationException("Model too large for glb.");

            using FileStream fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write(Magic);
            bw.Write(2u);
            bw.Write((uint)total);

            bw.Write((uint)(json.Length + jsonPad));
            bw.Write(ChunkJson);
            bw.Write(json);
            for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);

            if (binLength > 0)
            {
                bw.Write((uint)(binLength + binPad));
                bw.Write(ChunkBin);
                bw.Write(bin);
                for (int i = 0; i < binPad; i++) bw.Write((byte)0);
            }
        }

        private static string PartName(Part p) => string.IsNullOrWhiteSpace(p.Name) ? "part" : p.Name.Trim();

        // Shortest round-trip text, so the reader gets the identical value back.
        private static string F(float f) => f.ToString("R", Inv);

        private static string D(double d) => d.ToString("R", Inv);
    }
}
