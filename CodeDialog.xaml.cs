using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Distraint
{
    /// <summary>
    /// "Type this code by hand" speed bump (port of the protection dialog in SettingsActivity). A fresh 16-character code
    /// every time; look-alike characters (I, l, 1, O, 0) are left out; pasting and drag-and-drop are blocked.
    /// DialogResult is true only when the typed text matches.
    /// </summary>
    public partial class CodeDialog : Window
    {
        private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        private const string Lower = "abcdefghijkmnpqrstuvwxyz";
        private const string Digits = "23456789";
        private const string Symbols = "!@#$%&*?+=-";

        private readonly string code = NewCode();

        public CodeDialog(string title, string body)
        {
            InitializeComponent();
            TitleText.Text = title;
            BodyText.Text = body;
            CodeText.Text = code.Substring(0, 4) + "  " + code.Substring(4, 4) + "  " + code.Substring(8, 4) + "  " + code.Substring(12, 4);

            // Best-effort paste blocking: Ctrl+V / Shift+Insert, the paste command itself, and drag-and-drop.
            DataObject.AddPastingHandler(CodeBox, (s, e) => e.CancelCommand());
            CodeBox.PreviewKeyDown += (s, e) =>
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if ((ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert)) e.Handled = true;
            };
            CodeBox.PreviewDragOver += (s, e) => { e.Effects = DragDropEffects.None; e.Handled = true; };
            Loaded += (s, e) => CodeBox.Focus();
        }

        /// <summary>16 random characters from a cryptographically secure generator, at least one of each kind.</summary>
        private static string NewCode()
        {
            string all = Upper + Lower + Digits + Symbols;
            var chars = new List<char>
            {
                Pick(Upper), Pick(Lower), Pick(Digits), Pick(Symbols)   // one of each kind first...
            };
            while (chars.Count < 16) chars.Add(Pick(all));
            for (int i = chars.Count - 1; i > 0; i--)                   // ...then shuffle so they don't sit at the front
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                char tmp = chars[i]; chars[i] = chars[j]; chars[j] = tmp;
            }
            var sb = new StringBuilder();
            foreach (char c in chars) sb.Append(c);
            return sb.ToString();
        }

        private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (CodeBox.Text == code) DialogResult = true;
            else ErrorText.Text = "That doesn't match.";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
