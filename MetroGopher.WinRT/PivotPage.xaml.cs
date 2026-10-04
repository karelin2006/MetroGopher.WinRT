using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Collections;
using Windows.Media.Playback;
using Windows.Phone.UI.Input;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Notifications;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using MetroGopher.WinRT.Models;
using MetroGopher.WinRT.Services;

namespace MetroGopher.WinRT
{
    public sealed partial class PivotPage : Page
    {
        private readonly GopherClient _client = new GopherClient();
        private readonly Stack<GopherHistoryItem> _historyStack = new Stack<GopherHistoryItem>();
        private bool _isNavigatingHistory = false;

        private string _currentHost = "gopher.floodgap.com";
        private int _currentPort = 70;
        private string _currentSelector = "";
        private GopherItem _currentDocument = null;

        private readonly DispatcherTimer _autoRefreshTimer = new DispatcherTimer();

        // Альбомный плейлист текущей папки
        private readonly List<GopherItem> _currentPlaylist = new List<GopherItem>();
        private int _currentTrackIndex = -1;
        private bool _isMediaPlayerSubscribed = false;

        private readonly string[] _knownGopherServers =
        {
            "ezhe.ddns.net", "gopher.debene.dev", "gopher.floodgap.com", "sdf.org", "gopher.viste.fr",
            "bitreich.org", "quux.org", "gopher.black", "gopher.navigo.com", "1436.ninja", "gopher.club",
            "gopher.ddis.ch", "gopher.fman.com", "hngopher.com", "gopher.linkerror.com", "magical.city",
            "gopher.tildeverse.org", "gopher.icu", "gopher.top", "phreaknet.org", "gopher.space"
        };

        private static readonly Regex UrlRegex = new Regex(@"(gopher://[^\s<>""']+|https?://[^\s<>""']+|git://[^\s<>""']+|www\.[^\s<>""']+|github\.com/[^\s<>""']+)", RegexOptions.IgnoreCase);

        public ObservableCollection<GopherBookmark> Bookmarks { get; set; }
        public ObservableCollection<GopherHistoryItem> History { get; set; }
        private readonly ObservableCollection<GopherItem> _gopherItems = new ObservableCollection<GopherItem>();

        private const string BookmarksKey = "SavedBookmarks";
        private const string HistoryKey = "SavedHistory";

        public PivotPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;

            Bookmarks = new ObservableCollection<GopherBookmark>();
            History = new ObservableCollection<GopherHistoryItem>();

            LoadData();

            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(5);
            _autoRefreshTimer.Tick += (s, ev) => SilentRefreshDocument();

            GopherList.ItemsSource = _gopherItems;
            BookmarksList.ItemsSource = Bookmarks;
            HistoryList.ItemsSource = History;

            HardwareButtons.BackPressed += HardwareButtons_BackPressed;

            LoadGopherPage(_currentHost, _currentPort, _currentSelector);
        }

        #region Обработка кнопки Назад

        private void HardwareButtons_BackPressed(object sender, BackPressedEventArgs e)
        {
            if (MainPivot.SelectedItem == PivotDocument)
            {
                MainPivot.SelectedIndex = 0;
                _currentDocument = null;
                _autoRefreshTimer.Stop();
                e.Handled = true;
                return;
            }

            if (_historyStack.Count > 1)
            {
                e.Handled = true;
                _historyStack.Pop();
                var previousPage = _historyStack.Peek();
                _isNavigatingHistory = true;
                LoadGopherPage(previousPage.Host, previousPage.Port, previousPage.Selector, false);
            }
        }

        #endregion

        #region Отображение данных

        private void DisplayLongText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                DocumentListBox.ItemsSource = null;
                return;
            }

            DocumentListBox.ItemTemplate = null;

            var rawLines = text.Split(new[] { '\n' }, StringSplitOptions.None);
            var visualItems = new List<UIElement>(rawLines.Length);

            bool previousWasEmpty = false;

            foreach (var rawLine in rawLines)
            {
                string line = rawLine.TrimEnd('\r');

                // Нормализация табуляции во избежание сдвига вправо
                if (line.Contains("\t"))
                {
                    line = line.Replace("\t", "    ");
                }

                bool isEmpty = string.IsNullOrWhiteSpace(line);

                if (isEmpty && previousWasEmpty)
                {
                    continue;
                }

                visualItems.Add(BuildLineElement(line));
                previousWasEmpty = isEmpty;
            }

            DocumentListBox.ItemsSource = visualItems;
            MainPivot.SelectedItem = PivotDocument;
        }

        private UIElement BuildLineElement(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return new Border { Height = 8 };
            }

            var whiteBrush = new SolidColorBrush(Colors.White);

            var tb = new TextBlock
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = whiteBrush,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 12.0,
                LineHeight = 14.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };

            var matches = UrlRegex.Matches(line);

            if (matches.Count == 0)
            {
                tb.Text = line;
            }
            else
            {
                int currentIndex = 0;
                foreach (Match match in matches)
                {
                    if (match.Index > currentIndex)
                    {
                        tb.Inlines.Add(new Run
                        {
                            Text = line.Substring(currentIndex, match.Index - currentIndex),
                            Foreground = whiteBrush
                        });
                    }

                    string rawUrl = match.Value;
                    var link = new Hyperlink
                    {
                        Foreground = (SolidColorBrush)Application.Current.Resources["PhoneAccentBrush"]
                    };

                    link.Inlines.Add(new Run
                    {
                        Text = rawUrl,
                        FontFamily = new FontFamily("Consolas, Courier New"),
                        FontSize = 12.0
                    });

                    link.Click += async (s, e) =>
                    {
                        await RouteLinkAsync(rawUrl);
                    };

                    tb.Inlines.Add(link);
                    currentIndex = match.Index + match.Length;
                }

                if (currentIndex < line.Length)
                {
                    tb.Inlines.Add(new Run
                    {
                        Text = line.Substring(currentIndex),
                        Foreground = whiteBrush
                    });
                }
            }

            return tb;
        }

        private async Task RouteLinkAsync(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return;
            string clean = rawUrl.Trim();

            if (clean.StartsWith("gopher://", StringComparison.OrdinalIgnoreCase))
            {
                MainPivot.SelectedIndex = 0;
                ExecuteAddressNavigation(clean);
                return;
            }

            await OpenExternalWebUriAsync(clean);
        }

        #endregion

        #region Хранилище ApplicationData

        private void LoadData()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings.Values;

                if (settings.ContainsKey(BookmarksKey))
                {
                    string savedBookmarks = settings[BookmarksKey] as string;
                    if (!string.IsNullOrEmpty(savedBookmarks))
                    {
                        Bookmarks.Clear();
                        string[] lines = savedBookmarks.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var item in lines)
                        {
                            var parts = item.Split('|');
                            if (parts.Length >= 5)
                            {
                                int port;
                                if (!int.TryParse(parts[2], out port) || port <= 0)
                                    port = 70;

                                GopherItemType itemType = GopherItemType.Directory;
                                try { itemType = (GopherItemType)Enum.Parse(typeof(GopherItemType), parts[4]); } catch { }

                                Bookmarks.Add(new GopherBookmark
                                {
                                    Title = parts[0],
                                    Host = parts[1],
                                    Port = port,
                                    Selector = parts[3],
                                    ItemType = itemType
                                });
                            }
                        }
                    }
                }

                if (settings.ContainsKey(HistoryKey))
                {
                    string savedHistory = settings[HistoryKey] as string;
                    if (!string.IsNullOrEmpty(savedHistory))
                    {
                        History.Clear();
                        string[] lines = savedHistory.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var item in lines)
                        {
                            var parts = item.Split('|');
                            if (parts.Length >= 3)
                            {
                                int port;
                                if (!int.TryParse(parts[1], out port) || port <= 0)
                                    port = 70;

                                GopherItemType itemType = GopherItemType.Directory;
                                if (parts.Length >= 4)
                                {
                                    try { itemType = (GopherItemType)Enum.Parse(typeof(GopherItemType), parts[3]); } catch { }
                                }

                                History.Add(new GopherHistoryItem { Host = parts[0], Port = port, Selector = parts[2], ItemType = itemType });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SETTINGS] LoadData error: " + ex.Message);
            }
        }

        private void SaveData()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings.Values;

                string bookmarksPayload = string.Join("\n", Bookmarks.Select(b =>
                    string.Format("{0}|{1}|{2}|{3}|{4}", b.Title, b.Host, b.Port, b.Selector, b.ItemType)));

                string historyPayload = string.Join("\n", History.Select(h =>
                    string.Format("{0}|{1}|{2}|{3}", h.Host, h.Port, h.Selector, h.ItemType)));

                settings[BookmarksKey] = bookmarksPayload;
                settings[HistoryKey] = historyPayload;
            }
            catch { }
        }

        private void SaveToPermanentHistory(string host, int port, string selector, GopherItemType type)
        {
            if (string.IsNullOrEmpty(host) || _isNavigatingHistory) return;

            var existing = History.FirstOrDefault(h => h.Host == host && h.Port == port && h.Selector == selector);
            if (existing != null)
            {
                History.Remove(existing);
            }

            History.Insert(0, new GopherHistoryItem { Host = host, Port = port, Selector = selector, ItemType = type });

            if (History.Count > 100)
            {
                History.RemoveAt(History.Count - 1);
            }

            SaveData();
        }

        #endregion

        #region Навигация

        private void OnGoClick(object sender, RoutedEventArgs e)
        {
            ExecuteAddressNavigation(AddressBox.Text.Trim());
        }

        private void AddressBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem != null)
            {
                ExecuteAddressNavigation(args.SelectedItem.ToString());
            }
        }

        private void AddressBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                string query = sender.Text.ToLowerInvariant();
                var suggestions = _knownGopherServers
                    .Concat(History.Select(h => h.Host))
                    .Where(s => s.ToLowerInvariant().Contains(query))
                    .Distinct()
                    .Take(5)
                    .ToList();
                sender.ItemsSource = suggestions;
            }
        }

        private void ExecuteAddressNavigation(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;

            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Task.Run(async () => await OpenExternalWebUriAsync(input));
                return;
            }

            if (!input.Contains(".") && !input.Contains(":") && !input.Contains("/"))
            {
                LoadGopherPage("gopher.floodgap.com", 70, "/v2/vs\t" + input);
                return;
            }

            string host;
            int port;
            string selector;
            ParseAddress(input, out host, out port, out selector);
            LoadGopherPage(host, port, selector);
        }

        private void ParseAddress(string input, out string host, out int port, out string selector)
        {
            host = _currentHost;
            port = _currentPort;
            selector = "";

            if (string.IsNullOrWhiteSpace(input)) return;

            string s = input.Trim();
            if (s.StartsWith("gopher://", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(9);

            int slash = s.IndexOf('/');
            if (slash >= 0)
            {
                selector = s.Substring(slash);
                s = s.Substring(0, slash);
            }

            int colon = s.LastIndexOf(':');
            if (colon > 0 && colon < s.Length - 1)
            {
                host = s.Substring(0, colon);
                int parsedPort;
                if (int.TryParse(s.Substring(colon + 1), out parsedPort) && parsedPort > 0)
                    port = parsedPort;
                else
                    port = 70;
            }
            else if (!string.IsNullOrEmpty(s))
            {
                host = s;
            }
        }

        private async void LoadGopherPage(string host, int port, string selector, bool addToHistory = true, bool forceRefresh = false)
        {
            _currentHost = host;
            _currentPort = port;
            _currentSelector = selector;
            _currentDocument = null;

            LoadingBar.Visibility = Visibility.Visible;
            _gopherItems.Clear();

            string displaySelector = selector.Contains("\t") ? selector.Split('\t')[0] : selector;
            AddressBox.Text = port == 70 ? host + displaySelector : string.Format("{0}:{1}{2}", host, port, displaySelector);

            if (addToHistory && !_isNavigatingHistory && !string.IsNullOrEmpty(host))
            {
                _historyStack.Push(new GopherHistoryItem { Host = host, Port = port, Selector = selector, ItemType = GopherItemType.Directory });
                SaveToPermanentHistory(host, port, selector, GopherItemType.Directory);
            }

            try
            {
                var items = await _client.GetDirectoryAsync(host, port, selector);

                _currentPlaylist.Clear();
                _currentTrackIndex = -1;

                foreach (var item in items)
                {
                    _gopherItems.Add(item);
                    if (item.ItemType == GopherItemType.Audio)
                    {
                        _currentPlaylist.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                var dlg = new MessageDialog(ex.Message, "Network Error");
                await dlg.ShowAsync();
            }
            finally
            {
                LoadingBar.Visibility = Visibility.Collapsed;
                _isNavigatingHistory = false;
            }
        }

        #endregion

        #region Диспетчеризация элементов меню

        private async void OnGopherItemClicked(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as GopherItem;
            if (item == null) return;

            if (!string.IsNullOrEmpty(item.Selector) &&
                (item.Selector.StartsWith("URL:", StringComparison.OrdinalIgnoreCase) ||
                 item.Selector.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 item.Selector.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                await OpenExternalWebUriAsync(item.Selector);
                return;
            }

            if (item.ItemType == GopherItemType.Audio)
            {
                await PlayAudioAsync(item);
                return;
            }

            switch (item.ItemType)
            {
                case GopherItemType.Directory:
                    LoadGopherPage(item.Host, item.Port, item.Selector);
                    break;
                case GopherItemType.TextFile:
                    await OpenTextFileAsync(item);
                    break;
                case GopherItemType.Search:
                    ShowSearchDialog(item);
                    break;
                case GopherItemType.HtmlLink:
                    await OpenExternalWebUriAsync(item.Selector);
                    break;
                case GopherItemType.Telnet:
                    OpenTelnet(item);
                    break;
                default:
                    await DownloadAndSaveFileAsync(item);
                    break;
            }
        }

        #endregion

        #region Фоновое аудио

        private void EnsureMediaPlayerSubscribed()
        {
            if (!_isMediaPlayerSubscribed)
            {
                try
                {
                    BackgroundMediaPlayer.MessageReceivedFromBackground += OnMediaPlayerMessageReceived;
                    _isMediaPlayerSubscribed = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[UI] Ошибка подписки на BackgroundMediaPlayer: " + ex.Message);
                }
            }
        }

        private void ShowTrackToast(string title, string artist)
        {
            try
            {
                XmlDocument toastXml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var textNodes = toastXml.GetElementsByTagName("text");
                textNodes[0].AppendChild(toastXml.CreateTextNode("▶ " + (title ?? "Audio Track")));
                textNodes[1].AppendChild(toastXml.CreateTextNode(artist ?? "MetroGopher"));

                var audioElement = toastXml.CreateElement("audio");
                audioElement.SetAttribute("silent", "true");
                toastXml.DocumentElement.AppendChild(audioElement);

                ToastNotification toast = new ToastNotification(toastXml);
                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch { }
        }

        private string GetCacheFileName(GopherItem item)
        {
            uint hash = 2166136261;
            string key = item.Host + "_" + item.Selector;
            foreach (char c in key)
            {
                hash = (hash ^ c) * 16777619;
            }
            return string.Format("cache_{0:X}.mp3", hash);
        }

        private async Task<StorageFile> GetOrDownloadTrackAsync(GopherItem item)
        {
            string cacheName = GetCacheFileName(item);
            StorageFolder folder = ApplicationData.Current.LocalFolder;

            try
            {
                var existing = await folder.GetFileAsync(cacheName);
                var props = await existing.GetBasicPropertiesAsync();
                if (props.Size > 0)
                {
                    return existing;
                }
            }
            catch { }

            return await _client.DownloadBinaryToFileAsync(item.Host, item.Port, item.Selector, cacheName);
        }

        private void PrefetchNextTrack()
        {
            int nextIndex = _currentTrackIndex + 1;
            if (nextIndex < _currentPlaylist.Count)
            {
                var nextItem = _currentPlaylist[nextIndex];
                Task.Run(async () =>
                {
                    try
                    {
                        await GetOrDownloadTrackAsync(nextItem);
                    }
                    catch { }
                });
            }
        }

        private async Task PlayAudioAsync(GopherItem item)
        {
            if (item == null) return;

            _currentTrackIndex = _currentPlaylist.IndexOf(item);
            await PlayTrackAtIndexAsync(_currentTrackIndex >= 0 ? _currentTrackIndex : 0, item);
        }

        private async Task PlayTrackAtIndexAsync(int index, GopherItem directItem = null)
        {
            GopherItem targetTrack = directItem;

            if (targetTrack == null && index >= 0 && index < _currentPlaylist.Count)
            {
                targetTrack = _currentPlaylist[index];
                _currentTrackIndex = index;
            }

            if (targetTrack == null) return;

            LoadingBar.Visibility = Visibility.Visible;

            try
            {
                // Инициируем пробуждение процесса BackgroundPlayback параллельно со скачиванием
                EnsureMediaPlayerSubscribed();
                var wakeUpTask = Task.Run(() =>
                {
                    try { var dummy = BackgroundMediaPlayer.Current.CurrentState; } catch { }
                });

                StorageFile trackFile = await GetOrDownloadTrackAsync(targetTrack);

                var fileProps = await trackFile.GetBasicPropertiesAsync();
                if (fileProps.Size == 0)
                {
                    var emptyDlg = new MessageDialog("Файл пуст (0 байт)", "Ошибка аудио");
                    await emptyDlg.ShowAsync();
                    return;
                }

                await wakeUpTask;

                var msg = new ValueSet();
                msg.Add("command", "play");
                msg.Add("fileName", trackFile.Name);
                msg.Add("title", targetTrack.Title ?? "Audio Track");
                msg.Add("artist", targetTrack.Host ?? "Gopher Server");

                // Гарантированная отправка команды в просыпающийся фоновый процесс
                bool sent = false;
                for (int attempt = 0; attempt < 4 && !sent; attempt++)
                {
                    try
                    {
                        BackgroundMediaPlayer.SendMessageToBackground(msg);
                        sent = true;
                    }
                    catch
                    {
                        await Task.Delay(200);
                    }
                }

                ShowTrackToast(targetTrack.Title, targetTrack.Host);
                PrefetchNextTrack();
            }
            catch (Exception ex)
            {
                var dlg = new MessageDialog("Ошибка аудио:\n" + ex.Message, "Player Error");
                await dlg.ShowAsync();
            }
            finally
            {
                LoadingBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void PlayNextTrackAsync()
        {
            if (_currentPlaylist.Count == 0) return;

            int nextIndex = _currentTrackIndex + 1;
            if (nextIndex < _currentPlaylist.Count)
            {
                await PlayTrackAtIndexAsync(nextIndex);
            }
        }

        private async void PlayPreviousTrackAsync()
        {
            if (_currentPlaylist.Count == 0) return;

            int prevIndex = _currentTrackIndex - 1;
            if (prevIndex >= 0)
            {
                await PlayTrackAtIndexAsync(prevIndex);
            }
            else
            {
                await PlayTrackAtIndexAsync(0);
            }
        }

        private async void OnMediaPlayerMessageReceived(object sender, MediaPlayerDataReceivedEventArgs e)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (e.Data.ContainsKey("event"))
                {
                    string ev = e.Data["event"] as string;
                    if (ev == "track_ended" || ev == "user_next")
                    {
                        PlayNextTrackAsync();
                    }
                    else if (ev == "user_previous")
                    {
                        PlayPreviousTrackAsync();
                    }
                }
            });
        }

        #endregion

        #region Форматы текста, ссылок и Telnet

        private async Task OpenTextFileAsync(GopherItem item)
        {
            LoadingBar.Visibility = Visibility.Visible;
            SaveToPermanentHistory(item.Host, item.Port, item.Selector, item.ItemType);

            try
            {
                var file = await _client.DownloadBinaryToFileAsync(item.Host, item.Port, item.Selector, "temp_doc.txt");
                string content = await FileIO.ReadTextAsync(file);

                DisplayLongText(content);
                _currentDocument = item;
            }
            catch (Exception ex)
            {
                var dlg = new MessageDialog("Не удалось открыть файл:\n" + ex.Message, "Error");
                await dlg.ShowAsync();
            }
            finally
            {
                LoadingBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void OpenWebLink(GopherItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Selector)) return;
            await OpenExternalWebUriAsync(item.Selector);
        }

        private async Task OpenExternalWebUriAsync(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return;

            string clean = rawUrl.Trim();

            if (clean.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(4).Trim();
            }

            if (clean.Contains("\t"))
            {
                clean = clean.Split('\t')[0].Trim();
            }

            if (clean.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
            {
                clean = "https://" + clean.Substring(6);
            }

            if (!clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                clean = "https://" + clean;
            }

            try
            {
                Uri targetUri;
                if (Uri.TryCreate(clean, UriKind.Absolute, out targetUri))
                {
                    await Launcher.LaunchUriAsync(targetUri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BROWSER] Ошибка открытия браузера: " + ex.Message);
            }
        }

        private async void OpenTelnet(GopherItem item)
        {
            string telnetUri = string.Format("telnet://{0}:{1}", item.Host, item.Port);
            try
            {
                await Launcher.LaunchUriAsync(new Uri(telnetUri));
            }
            catch
            {
                var dlg = new MessageDialog(string.Format("Сессия Telnet:\nХост: {0}\nПорт: {1}\nСелектор: {2}", item.Host, item.Port, item.Selector), "Telnet");
                await dlg.ShowAsync();
            }
        }

        private async void ShowSearchDialog(GopherItem item)
        {
            var inputTextBox = new TextBox { PlaceholderText = "Поисковый запрос" };
            var dialog = new ContentDialog
            {
                Title = item.Title ?? "GOPHER SEARCH",
                Content = inputTextBox,
                PrimaryButtonText = "Поиск",
                SecondaryButtonText = "Отмена"
            };

            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(inputTextBox.Text))
            {
                string searchSelector = item.Selector + "\t" + inputTextBox.Text.Trim();
                LoadGopherPage(item.Host, item.Port, searchSelector);
            }
        }

        private async Task DownloadAndSaveFileAsync(GopherItem item)
        {
            LoadingBar.Visibility = Visibility.Visible;
            SaveToPermanentHistory(item.Host, item.Port, item.Selector, item.ItemType);

            try
            {
                string safeName = MakeSafeFileName(item.Title, item.ItemType);
                await _client.DownloadBinaryToFileAsync(item.Host, item.Port, item.Selector, safeName);

                var dlg = new MessageDialog(string.Format("Файл успешно сохранен в хранилище приложения:\n{0}", safeName), "Загрузка завершена");
                await dlg.ShowAsync();
            }
            catch (Exception ex)
            {
                var dlg = new MessageDialog("Ошибка загрузки:\n" + ex.Message, "Error");
                await dlg.ShowAsync();
            }
            finally
            {
                LoadingBar.Visibility = Visibility.Collapsed;
            }
        }

        private string MakeSafeFileName(string title, GopherItemType type)
        {
            if (string.IsNullOrWhiteSpace(title)) title = "file";
            foreach (char c in Path.GetInvalidFileNameChars())
                title = title.Replace(c, '_');

            title = title.Trim();
            if (title.Length > 60) title = title.Substring(0, 60);

            string ext = Path.GetExtension(title);
            if (string.IsNullOrEmpty(ext))
            {
                ext = type == GopherItemType.Audio ? ".mp3" : ".dat";
                title += ext;
            }
            return title;
        }

        #endregion

        #region Дополнительные обработчики

        private void OnPivotSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainPivot.SelectedItem == PivotDocument)
            {
                _autoRefreshTimer.Start();
            }
            else
            {
                _autoRefreshTimer.Stop();
            }
        }

        private async void SilentRefreshDocument()
        {
            if (_currentDocument == null || MainPivot.SelectedItem != PivotDocument) return;

            try
            {
                var file = await _client.DownloadBinaryToFileAsync(_currentDocument.Host, _currentDocument.Port, _currentDocument.Selector, "temp_doc.txt");
                string content = await FileIO.ReadTextAsync(file);
                DisplayLongText(content);
            }
            catch { }
        }

        private async void OnBookmarkClick(object sender, RoutedEventArgs e)
        {
            var bm = new GopherBookmark
            {
                Title = string.IsNullOrWhiteSpace(AddressBox.Text) ? "Page" : AddressBox.Text,
                Host = _currentHost,
                Port = _currentPort,
                Selector = _currentSelector,
                ItemType = GopherItemType.Directory
            };

            Bookmarks.Add(bm);
            SaveData();

            var dlg = new MessageDialog("Страница сохранена в закладки!", "Закладки");
            await dlg.ShowAsync();
        }

        private void OnBookmarkSelected(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            var bm = btn.Tag as GopherBookmark;
            if (bm == null) return;

            MainPivot.SelectedIndex = 0;
            LoadGopherPage(bm.Host, bm.Port, bm.Selector);
        }

        private void OnDeleteBookmarkClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            var bm = btn.Tag as GopherBookmark;
            if (bm == null) return;

            Bookmarks.Remove(bm);
            SaveData();
        }

        private void OnClearBookmarksClick(object sender, RoutedEventArgs e)
        {
            Bookmarks.Clear();
            SaveData();
        }

        private void OnHistorySelected(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            var h = btn.Tag as GopherHistoryItem;
            if (h == null) return;

            MainPivot.SelectedIndex = 0;
            LoadGopherPage(h.Host, h.Port, h.Selector);
        }

        private void OnDeleteHistoryItemClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            var h = btn.Tag as GopherHistoryItem;
            if (h == null) return;

            History.Remove(h);
            SaveData();
        }

        private void OnClearHistoryClick(object sender, RoutedEventArgs e)
        {
            History.Clear();
            SaveData();
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (MainPivot.SelectedItem == PivotDocument && _currentDocument != null)
            {
                SilentRefreshDocument();
            }
            else if (MainPivot.SelectedIndex == 0 && !string.IsNullOrEmpty(_currentHost))
            {
                LoadGopherPage(_currentHost, _currentPort, _currentSelector, false, true);
            }
        }

        private void OnHomeClick(object sender, RoutedEventArgs e)
        {
            MainPivot.SelectedIndex = 0;
            LoadGopherPage("gopher.floodgap.com", 70, "");
        }

        #endregion
    }
}