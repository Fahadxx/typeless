using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Typeless
{
    public static class Converter
    {
        // Converts src to target next to the original. Returns final output paths in page order.
        public static List<string> Convert(string src, string target)
        {
            src = Path.GetFullPath(src);
            target = Formats.Norm(target);

            if (!File.Exists(src)) throw new FileNotFoundException($"File not found: {src}", src);

            Kind kind = Formats.KindOf(src)
                ?? throw new NotSupportedException($"Unsupported file type: {Path.GetFileName(src)}");

            if (!Formats.TargetsFor(src).Contains(target))
                throw new NotSupportedException(
                    $"Cannot convert {Formats.Norm(src)} to {target} (unsupported target or missing backend).");

            string work = Path.Combine(Path.GetTempPath(), "Typeless", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                List<string> temps = kind switch
                {
                    Kind.Image => ImageBackend.Convert(src, target, work),
                    Kind.Pdf => PdfBackend.Convert(src, target, work),
                    Kind.Document or Kind.Sheet or Kind.Slides => OfficeBackend.Convert(kind, src, target, work),
                    Kind.Audio or Kind.Video => MediaBackend.Convert(kind, src, target, work),
                    _ => throw new NotSupportedException($"No backend for {kind}."),
                };

                if (temps.Count == 0) throw new InvalidOperationException("Conversion produced no output.");

                var finals = new List<string>(temps.Count);
                for (int i = 0; i < temps.Count; i++)
                {
                    string dest = temps.Count == 1 ? Naming.Final(src, target) : Naming.Final(src, target, i + 1);
                    File.Move(temps[i], dest);
                    finals.Add(dest);
                }
                return finals;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        // Runs f on a dedicated STA background thread (COM backends such as Word need STA).
        public static Task<T> RunSta<T>(Func<T> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(f()); }
                catch (Exception ex) { tcs.SetException(ex); }
            })
            {
                IsBackground = true,
                Name = "Typeless STA worker",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }
    }
}
