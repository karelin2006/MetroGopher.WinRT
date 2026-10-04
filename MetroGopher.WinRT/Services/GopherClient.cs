using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using MetroGopher.WinRT.Models;

namespace MetroGopher.WinRT.Services
{
    public class GopherClient
    {
        private const int DefaultTimeoutSeconds = 15;
        private const uint NetworkBufferSize = 64 * 1024; // 64 КБ

        public async Task<string> FetchTextAsync(string host, int port, string selector, string searchQuery = null)
        {
            byte[] data = await RawRequestBytesAsync(host, port, selector, searchQuery);
            if (data == null || data.Length == 0)
                return string.Empty;

            try
            {
                var utf8Strict = new UTF8Encoding(false, true);
                return utf8Strict.GetString(data, 0, data.Length);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding("ISO-8859-1").GetString(data, 0, data.Length);
            }
        }

        public async Task<List<GopherItem>> GetDirectoryAsync(string host, int port, string selector)
        {
            string rawResponse = await FetchTextAsync(host, port, selector);
            return ParseMenu(rawResponse, host, port);
        }

        public async Task<StorageFile> DownloadBinaryToFileAsync(string host, int port, string selector, string targetFileName)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Хост не может быть пустым.");

            if (port <= 0)
                port = 70;

            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFile file = await localFolder.CreateFileAsync(targetFileName, CreationCollisionOption.ReplaceExisting);

            using (var fileStream = await file.OpenStreamForWriteAsync())
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            using (var socket = new StreamSocket())
            {
                socket.Control.NoDelay = true;

                await socket.ConnectAsync(new HostName(host), port.ToString()).AsTask(cts.Token);

                string request = (selector ?? string.Empty) + "\r\n";
                byte[] requestBytes = Encoding.UTF8.GetBytes(request);

                using (var writer = new DataWriter(socket.OutputStream))
                {
                    writer.WriteBytes(requestBytes);
                    await writer.StoreAsync().AsTask(cts.Token);
                    await writer.FlushAsync().AsTask(cts.Token);
                    writer.DetachStream();
                }

                using (var reader = new DataReader(socket.InputStream))
                {
                    reader.InputStreamOptions = InputStreamOptions.Partial;

                    while (true)
                    {
                        uint loaded = await reader.LoadAsync(NetworkBufferSize).AsTask(cts.Token);
                        if (loaded == 0)
                            break;

                        byte[] chunk = new byte[loaded];
                        reader.ReadBytes(chunk);

                        await fileStream.WriteAsync(chunk, 0, (int)loaded, cts.Token);
                    }

                    await fileStream.FlushAsync(cts.Token);
                }
            }

            return file;
        }

        public async Task<byte[]> RawRequestBytesAsync(string host, int port, string selector, string searchQuery = null)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Хост не может быть пустым.");

            if (port <= 0)
                port = 70;

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(DefaultTimeoutSeconds)))
            using (var socket = new StreamSocket())
            {
                socket.Control.NoDelay = true;

                try
                {
                    await socket.ConnectAsync(new HostName(host), port.ToString()).AsTask(cts.Token);

                    string request = selector ?? string.Empty;
                    if (!string.IsNullOrEmpty(searchQuery))
                    {
                        request += "\t" + searchQuery;
                    }
                    request += "\r\n";

                    byte[] requestBytes = Encoding.UTF8.GetBytes(request);

                    using (var writer = new DataWriter(socket.OutputStream))
                    {
                        writer.WriteBytes(requestBytes);
                        await writer.StoreAsync().AsTask(cts.Token);
                        await writer.FlushAsync().AsTask(cts.Token);
                        writer.DetachStream();
                    }

                    using (var reader = new DataReader(socket.InputStream))
                    {
                        reader.InputStreamOptions = InputStreamOptions.Partial;
                        using (var ms = new MemoryStream())
                        {
                            while (true)
                            {
                                uint loaded = await reader.LoadAsync(NetworkBufferSize).AsTask(cts.Token);
                                if (loaded == 0)
                                    break;

                                byte[] chunk = new byte[loaded];
                                reader.ReadBytes(chunk);
                                ms.Write(chunk, 0, (int)loaded);
                            }
                            return ms.ToArray();
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    throw new TimeoutException(string.Format("Сервер {0}:{1} не ответил вовремя.", host, port));
                }
                catch (Exception ex)
                {
                    throw new Exception(string.Format("Сетевая ошибка ({0}): {1}", host, ex.Message));
                }
            }
        }

        public List<GopherItem> ParseMenu(string rawResponse, string currentHost, int currentPort)
        {
            var items = new List<GopherItem>();
            if (string.IsNullOrEmpty(rawResponse))
                return items;

            var lines = rawResponse.Replace("\r\n", "\n").Replace('\r', '\n')
                                   .Split(new[] { '\n' }, StringSplitOptions.None);

            GopherItem lastNonRedundantItem = null;
            bool previousWasEmptyInfo = false;

            foreach (var rawLine in lines)
            {
                if (string.IsNullOrEmpty(rawLine))
                    continue;

                if (rawLine == ".")
                    break;

                char typeChar = rawLine[0];
                string rest = rawLine.Length > 1 ? rawLine.Substring(1) : string.Empty;
                string[] parts = rest.Split('\t');

                string title = parts.Length > 0 ? parts[0] : string.Empty;
                string selector = parts.Length > 1 ? parts[1] : string.Empty;
                string host = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : currentHost;

                int port = currentPort;
                if (parts.Length > 3)
                {
                    int parsedPort;
                    if (int.TryParse(parts[3], out parsedPort) && parsedPort > 0)
                    {
                        port = parsedPort;
                    }
                }

                if (host.Equals("null.host", StringComparison.OrdinalIgnoreCase) ||
                    host.Equals("error.host", StringComparison.OrdinalIgnoreCase))
                {
                    host = currentHost;
                }

                var itemType = MapType(typeChar);

                // Определение аудио по расширению в селекторе
                if (itemType != GopherItemType.Audio && IsAudioExtension(selector))
                {
                    itemType = GopherItemType.Audio;
                }

                // Определение HTML / Web ссылок по селектору
                if (selector.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
                {
                    itemType = GopherItemType.HtmlLink;
                    selector = selector.Substring(4);
                }
                else if (itemType == GopherItemType.HtmlLink && selector.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
                {
                    selector = selector.Substring(4);
                }

                if (typeChar == '+' && lastNonRedundantItem != null)
                {
                    itemType = lastNonRedundantItem.ItemType;
                    if (string.IsNullOrEmpty(selector))
                        selector = lastNonRedundantItem.Selector;
                }

                bool isEmptyInfo = (itemType == GopherItemType.Info) && string.IsNullOrWhiteSpace(title);
                if (isEmptyInfo && previousWasEmptyInfo)
                {
                    continue;
                }

                var item = new GopherItem
                {
                    ItemType = itemType,
                    Title = title,
                    Selector = selector,
                    Host = host,
                    Port = port
                };

                items.Add(item);

                previousWasEmptyInfo = isEmptyInfo;

                if (typeChar != '+')
                {
                    lastNonRedundantItem = item;
                }
            }

            return items;
        }

        private bool IsAudioExtension(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return false;
            string lower = selector.ToLowerInvariant();
            return lower.EndsWith(".mp3") || lower.EndsWith(".wav") ||
                   lower.EndsWith(".ogg") || lower.EndsWith(".flac") ||
                   lower.EndsWith(".aac") || lower.EndsWith(".m4a");
        }

        public string CleanTextContent(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n')
                           .Split(new[] { '\n' }, StringSplitOptions.None);

            var sb = new StringBuilder();
            bool previousWasEmpty = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line == "." && i == lines.Length - 1)
                    break;

                if (line.StartsWith(".."))
                {
                    line = line.Substring(1);
                }

                bool isEmpty = string.IsNullOrWhiteSpace(line);
                if (isEmpty && previousWasEmpty)
                {
                    continue;
                }

                sb.AppendLine(line);
                previousWasEmpty = isEmpty;
            }

            return sb.ToString().TrimEnd('\r', '\n');
        }

        private GopherItemType MapType(char typeChar)
        {
            switch (typeChar)
            {
                case '0': return GopherItemType.TextFile;
                case '1': return GopherItemType.Directory;
                case '2': return GopherItemType.CSOPhone;
                case '3': return GopherItemType.Error;
                case '4': return GopherItemType.BinHex;
                case '5': return GopherItemType.DosBinary;
                case '6': return GopherItemType.Uuencoded;
                case '7': return GopherItemType.Search;
                case '8': return GopherItemType.Telnet;
                case '9': return GopherItemType.Binary;
                case '+': return GopherItemType.Redundant;
                case 'T': return GopherItemType.Tn3270;

                case 'g':
                case 'I':
                case 'p':
                case ':':
                    return GopherItemType.Image;

                case 's':
                case 'S':
                case '<':
                    return GopherItemType.Audio;

                case ';':
                    return GopherItemType.Video;

                case 'd':
                case 'D':
                case 'P':
                case 'M':
                    return GopherItemType.Document;

                case 'h':
                case 'H':
                    return GopherItemType.HtmlLink;

                case 'i':
                    return GopherItemType.Info;

                default:
                    return GopherItemType.Unknown;
            }
        }
    }
}