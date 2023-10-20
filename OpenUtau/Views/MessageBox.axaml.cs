﻿using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenUtau.App.Views {
    public partial class MessageBox : Window {
        public enum MessageBoxButtons { Ok, OkCancel, YesNo, YesNoCancel }
        public enum MessageBoxResult { Ok, Cancel, Yes, No }

        public MessageBox() {
            InitializeComponent();
        }

        public void SetText(string text) {
            Dispatcher.UIThread.Post(() => {
                Text.Text = text;
            });
        }

        public static Task<MessageBoxResult> ShowError(Window parent, Exception? e) {
            return ShowError(parent, string.Empty, e);
        }

        public static Task<MessageBoxResult> ShowError(Window parent, string message, Exception? e) {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(message)) {
                builder.AppendLine(message);
            }
            if (e != null) {
                if (e is AggregateException ae) {
                    ae = ae.Flatten();
                    builder.AppendLine(ae.InnerExceptions.First().Message);
                    builder.AppendLine();
                    builder.Append(ae.ToString());
                } else {
                    builder.AppendLine(e.Message);
                    builder.AppendLine();
                    builder.Append(e.ToString());
                }
            }
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown Version");
            string title = ThemeManager.GetString("errors.caption");
            return Show(parent, builder.ToString(), title, MessageBoxButtons.Ok);
        }

        public static Task<MessageBoxResult> Show(Window parent, string text, string title, MessageBoxButtons buttons) {
            var msgbox = new MessageBox() {
                Title = title
            };
            msgbox.Text.Text = text;

            var res = MessageBoxResult.Ok;

            void AddButton(string caption, MessageBoxResult r, bool def = false) {
                var btn = new Button { Content = caption };
                btn.Click += (_, __) => {
                    res = r;
                    msgbox.Close();
                };
                msgbox.Buttons.Children.Add(btn);
                if (def)
                    res = r;
            }

            if (buttons == MessageBoxButtons.Ok || buttons == MessageBoxButtons.OkCancel)
                AddButton(ThemeManager.GetString("dialogs.messagebox.ok"), MessageBoxResult.Ok, true);
            if (buttons == MessageBoxButtons.YesNo || buttons == MessageBoxButtons.YesNoCancel) {
                AddButton(ThemeManager.GetString("dialogs.messagebox.yes"), MessageBoxResult.Yes);
                AddButton(ThemeManager.GetString("dialogs.messagebox.no"), MessageBoxResult.No, true);
            }

            if (buttons == MessageBoxButtons.OkCancel || buttons == MessageBoxButtons.YesNoCancel)
                AddButton(ThemeManager.GetString("dialogs.messagebox.cancel"), MessageBoxResult.Cancel, true);

            var tcs = new TaskCompletionSource<MessageBoxResult>();
            msgbox.Closed += delegate { tcs.TrySetResult(res); };
            if (parent != null)
                msgbox.ShowDialog(parent);
            else msgbox.Show();
            return tcs.Task;
        }

        public static MessageBox ShowModal(Window parent, string text, string title) {
            var msgbox = new MessageBox() {
                Title = title
            };
            msgbox.Text.Text = text;
            msgbox.ShowDialog(parent);
            return msgbox;
        }

        public static MessageBox ShowLoading(Window parent) {
            var msgbox = new MessageBox() {
                Title = "Loading"
            };
            msgbox.Text.Text = "Please wait...";
            msgbox.Show(parent);
            return msgbox;
        }
    }
}
