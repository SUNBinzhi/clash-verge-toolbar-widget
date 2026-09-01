using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace ClashLeftWidget
{
    internal static class PipeHttp
    {
        public static async Task<string> GetAsync(string pipeName, string path, int timeoutMs)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await Task.Run(delegate { pipe.Connect(timeoutMs); });
                byte[] request = Encoding.ASCII.GetBytes(
                    "GET " + path + " HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
                await pipe.WriteAsync(request, 0, request.Length);
                await pipe.FlushAsync();

                using (var output = new MemoryStream())
                {
                    byte[] buffer = new byte[32768];
                    while (true)
                    {
                        var readTask = pipe.ReadAsync(buffer, 0, buffer.Length);
                        if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)) != readTask)
                            throw new TimeoutException();
                        int read = await readTask;
                        if (read <= 0) break;
                        output.Write(buffer, 0, read);
                        if (output.Length > 20 * 1024 * 1024)
                            throw new InvalidDataException("Mihomo API response is too large.");
                    }

                    byte[] raw = output.ToArray();
                    int headerEnd = FindHeaderEnd(raw);
                    if (headerEnd < 0) throw new InvalidDataException("Incomplete HTTP response.");
                    string headers = Encoding.ASCII.GetString(raw, 0, headerEnd);
                    if (!headers.StartsWith("HTTP/1.1 200") && !headers.StartsWith("HTTP/1.0 200"))
                        throw new InvalidDataException(headers.Split('\n')[0].Trim());

                    byte[] body = new byte[raw.Length - headerEnd - 4];
                    Buffer.BlockCopy(raw, headerEnd + 4, body, 0, body.Length);
                    if (headers.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                        body = DecodeChunked(body);
                    return Encoding.UTF8.GetString(body);
                }
            }
        }

        private static int FindHeaderEnd(byte[] data)
        {
            for (int i = 0; i <= data.Length - 4; i++)
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            return -1;
        }

        private static byte[] DecodeChunked(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                int position = 0;
                while (position < data.Length)
                {
                    int lineEnd = -1;
                    for (int i = position; i < data.Length - 1; i++)
                        if (data[i] == 13 && data[i + 1] == 10) { lineEnd = i; break; }
                    if (lineEnd < 0) throw new InvalidDataException("Missing chunk size.");
                    string sizeText = Encoding.ASCII.GetString(data, position, lineEnd - position).Split(';')[0];
                    int size = Convert.ToInt32(sizeText, 16);
                    position = lineEnd + 2;
                    if (size == 0) break;
                    if (position + size > data.Length) throw new InvalidDataException("Truncated chunked response.");
                    output.Write(data, position, size);
                    position += size + 2;
                }
                return output.ToArray();
            }
        }
    }
}
