namespace Sldl.Core.Models;
    public class SimpleFile
    {
        public string Path;
        public string? Artists;
        public string? Title;
        public string? Album;
        public int? Length;
        public int? Bitrate;
        public int? Samplerate;
        public int? Bitdepth;

        public SimpleFile(TagLib.File file)
        {
            Path = file.Name;
            SetFromTagLib(file);
        }

        public SimpleFile(string path)
        {
            try
            {
                var tagFile = TagLib.File.Create(path);
                Path = tagFile.Name;
                SetFromTagLib(tagFile);
            }
            catch (Exception ex)
            {
                Logger.Trace($"Failed to read tags for '{path}': {ex.Message}");
                Path = path;
            }
        }

        private void SetFromTagLib(TagLib.File file)
        {
            Path = file.Name;
            Artists = file.Tag.JoinedPerformers;
            Title = file.Tag.Title;
            Album = file.Tag.Album;
            Length = (int)file.Length;
            Bitrate = file.Properties.AudioBitrate;
            Samplerate = file.Properties.AudioSampleRate;
            Bitdepth = file.Properties.BitsPerSample;
        }
    }
