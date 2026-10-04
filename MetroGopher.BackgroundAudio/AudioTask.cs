using System;
using System.Diagnostics;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;

namespace MetroGopher.BackgroundAudio
{
    public sealed class AudioTask : IBackgroundTask
    {
        private BackgroundTaskDeferral _deferral;
        private SystemMediaTransportControls _smtc;
        private string _pendingTitle = "Audio Track";
        private string _pendingArtist = "MetroGopher";

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();
            Debug.WriteLine("[BG_AUDIO] Task Run started");

            InitSmtc();

            BackgroundMediaPlayer.MessageReceivedFromForeground += OnMessageReceivedFromForeground;

            BackgroundMediaPlayer.Current.CurrentStateChanged += (sender, args) =>
            {
                Debug.WriteLine("[BG_AUDIO] State: " + sender.CurrentState);

                if (_smtc != null)
                {
                    try
                    {
                        if (sender.CurrentState == MediaPlayerState.Playing)
                            _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                        else if (sender.CurrentState == MediaPlayerState.Paused)
                            _smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
                        else if (sender.CurrentState == MediaPlayerState.Closed || sender.CurrentState == MediaPlayerState.Stopped)
                            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                    }
                    catch { }
                }

                SendStateToUI(sender.CurrentState.ToString());
            };

            BackgroundMediaPlayer.Current.MediaOpened += (sender, args) =>
            {
                Debug.WriteLine("[BG_AUDIO] Media Opened successfully!");
                UpdateSmtcDisplay();
                SendStateToUI("Playing");
            };

            BackgroundMediaPlayer.Current.MediaEnded += (sender, args) =>
            {
                Debug.WriteLine("[BG_AUDIO] Media Ended");
                var response = new ValueSet();
                response.Add("event", "track_ended");
                BackgroundMediaPlayer.SendMessageToForeground(response);
            };

            BackgroundMediaPlayer.Current.MediaFailed += (sender, args) =>
            {
                Debug.WriteLine("[BG_AUDIO] Media Failed: 0x" + args.ExtendedErrorCode.HResult.ToString("X"));
                SendStateToUI("Failed");
            };

            taskInstance.Canceled += OnCanceled;
            taskInstance.Task.Completed += OnTaskCompleted;

            SendStateToUI("Ready");
        }

        private void InitSmtc()
        {
            try
            {
                _smtc = SystemMediaTransportControls.GetForCurrentView();
                _smtc.IsEnabled = true;
                _smtc.IsPlayEnabled = true;
                _smtc.IsPauseEnabled = true;
                _smtc.IsNextEnabled = true;
                _smtc.IsPreviousEnabled = true;
                _smtc.ButtonPressed += Smtc_ButtonPressed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BG_AUDIO] SMTC Init Error: " + ex.Message);
            }
        }

        private void Smtc_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    BackgroundMediaPlayer.Current.Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    BackgroundMediaPlayer.Current.Pause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    NotifyUIEvent("user_next");
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    NotifyUIEvent("user_previous");
                    break;
            }
        }

        private void NotifyUIEvent(string eventName)
        {
            var msg = new ValueSet();
            msg.Add("event", eventName);
            BackgroundMediaPlayer.SendMessageToForeground(msg);
        }

        private void UpdateSmtcDisplay()
        {
            if (_smtc == null) return;

            try
            {
                _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
                var updater = _smtc.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = _pendingTitle;
                updater.MusicProperties.Artist = _pendingArtist;
                updater.Update();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BG_AUDIO] SMTC Update error: " + ex.Message);
            }
        }

        private async void OnMessageReceivedFromForeground(object sender, MediaPlayerDataReceivedEventArgs e)
        {
            ValueSet message = e.Data;
            if (!message.ContainsKey("command")) return;

            string command = message["command"] as string;
            Debug.WriteLine("[BG_AUDIO] Command: " + command);

            if (command == "play")
            {
                if (message.ContainsKey("fileName"))
                {
                    string fileName = message["fileName"] as string;
                    if (message.ContainsKey("title")) _pendingTitle = message["title"] as string;
                    if (message.ContainsKey("artist")) _pendingArtist = message["artist"] as string;

                    try
                    {
                        StorageFolder localFolder = ApplicationData.Current.LocalFolder;
                        StorageFile file = await localFolder.GetFileAsync(fileName);

                        var props = await file.GetBasicPropertiesAsync();
                        if (props.Size == 0) return;

                        Uri fileUri = new Uri("ms-appdata:///local/" + fileName);

                        BackgroundMediaPlayer.Current.AutoPlay = true;
                        BackgroundMediaPlayer.Current.SetUriSource(fileUri);
                        BackgroundMediaPlayer.Current.Play();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[BG_AUDIO] Exception: " + ex.Message);
                    }
                }
            }
            else if (command == "pause")
            {
                BackgroundMediaPlayer.Current.Pause();
            }
            else if (command == "resume")
            {
                BackgroundMediaPlayer.Current.Play();
            }
            else if (command == "stop")
            {
                BackgroundMediaPlayer.Current.Pause();
                BackgroundMediaPlayer.Current.SetUriSource(null);
                if (_smtc != null)
                {
                    try { _smtc.PlaybackStatus = MediaPlaybackStatus.Closed; } catch { }
                }
            }
        }

        private void SendStateToUI(string state)
        {
            var response = new ValueSet();
            response.Add("state", state);
            BackgroundMediaPlayer.SendMessageToForeground(response);
        }

        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            try
            {
                if (_smtc != null)
                {
                    _smtc.ButtonPressed -= Smtc_ButtonPressed;
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                }
                BackgroundMediaPlayer.Shutdown();
            }
            finally
            {
                if (_deferral != null)
                {
                    _deferral.Complete();
                }
            }
        }

        private void OnTaskCompleted(BackgroundTaskRegistration sender, BackgroundTaskCompletedEventArgs args)
        {
            if (_deferral != null)
            {
                _deferral.Complete();
            }
        }
    }
}