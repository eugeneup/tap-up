using System;
using System.IO;
using System.Xml;

namespace TapUp
{
    internal sealed class ChestSettings
    {
        internal bool Enabled;
        internal bool MinimizeToTray;
        internal string GameDirectory = "";
        internal static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tap-Up", "chests.xml");
        internal bool Configured
        {
            get
            {
                try { return GamePatch.Installed(GameDirectory); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
                catch (System.Security.SecurityException) { return false; }
            }
        }
        internal ChestSettings Clone()
        {
            return new ChestSettings { Enabled = Enabled, MinimizeToTray = MinimizeToTray, GameDirectory = GameDirectory };
        }
        internal static ChestSettings Load(string path)
        {
            var settings = new ChestSettings();
            if (!File.Exists(path)) { settings.GameDirectory = GamePatch.FindGame(); return settings; }
            using (var stream = File.OpenRead(path))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true }))
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.Load(reader);
                if (doc.DocumentElement == null || doc.DocumentElement.Name != "chests") throw new InvalidDataException("Некорректные настройки сундуков");
                settings.Enabled = doc.DocumentElement.GetAttribute("enabled") == "true";
                settings.MinimizeToTray = doc.DocumentElement.GetAttribute("showTrayIcon") == "true";
                settings.GameDirectory = doc.DocumentElement.GetAttribute("game");
                // Version 1.3 coordinate templates are intentionally ignored.
                if (settings.GameDirectory.Length == 0) settings.GameDirectory = GamePatch.FindGame();
            }
            return settings;
        }
        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            try
            {
                using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("chests");
                    writer.WriteAttributeString("version", "2");
                    writer.WriteAttributeString("enabled", Enabled ? "true" : "false");
                    writer.WriteAttributeString("showTrayIcon", MinimizeToTray ? "true" : "false");
                    writer.WriteAttributeString("game", GameDirectory);
                    writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
