
using Sldl.Core.Jobs;

namespace Sldl.Core.Models;

    public class FolderConditions
    {
        public int? MinTrackCount;
        public int? MaxTrackCount;
        public List<string> RequiredTrackTitles = [];

        public FolderConditions() { }

        public FolderConditions(FolderConditions other)
        {
            MinTrackCount = other.MinTrackCount;
            MaxTrackCount = other.MaxTrackCount;
            RequiredTrackTitles = [.. other.RequiredTrackTitles];
        }

        public FolderConditionPatch AddConditions(FolderConditionPatch mod)
        {
            var undo = new FolderConditionPatch();

            if (mod.MinTrackCount != null)
            {
                undo.MinTrackCount = MinTrackCount;
                MinTrackCount      = mod.MinTrackCount;
            }
            if (mod.MaxTrackCount != null)
            {
                undo.MaxTrackCount = MaxTrackCount;
                MaxTrackCount      = mod.MaxTrackCount;
            }
            if (mod.RequiredTrackTitles?.Count > 0)
            {
                undo.RequiredTrackTitles = [.. RequiredTrackTitles];
                AddRequiredTrackTitles(mod.RequiredTrackTitles);
            }

            return undo;
        }

        public bool TrackCountSatisfies(int count)
        {
            if (MaxTrackCount != null && count > MaxTrackCount.Value) return false;
            if (MinTrackCount != null && MinTrackCount.Value > 0 && count < MinTrackCount.Value) return false;
            return true;
        }

        public void AddRequiredTrackTitle(string title)
        {
            if (title.Length > 0 && !RequiredTrackTitles.Contains(title))
                RequiredTrackTitles.Add(title);
        }

        public void AddRequiredTrackTitles(IEnumerable<string> titles)
        {
            foreach (var title in titles)
                AddRequiredTrackTitle(title);
        }

        public bool RequiredTrackTitlesSatisfy(IEnumerable<SongJob> files)
        {
            if (RequiredTrackTitles.Count == 0)
                return true;

            var fileList = files.ToList();
            var cond = new FileConditions { StrictTitle = true };
            return RequiredTrackTitles.All(title => fileList.Any(file => file.ResolvedTarget != null
                && cond.StrictTitleSatisfies(file.ResolvedTarget.Filename, title)));
        }
    }

    public class FolderConditionPatch
    {
        public int? MinTrackCount;
        public int? MaxTrackCount;
        public List<string>? RequiredTrackTitles;

        public bool IsEmpty()
            => MinTrackCount == null
            && MaxTrackCount == null
            && (RequiredTrackTitles == null || RequiredTrackTitles.Count == 0);

        public override bool Equals(object? obj)
        {
            if (obj is not FolderConditionPatch other)
                return false;

            return MinTrackCount == other.MinTrackCount
                && MaxTrackCount == other.MaxTrackCount
                && ((RequiredTrackTitles == null && other.RequiredTrackTitles == null)
                    || (RequiredTrackTitles != null && other.RequiredTrackTitles != null
                        && RequiredTrackTitles.SequenceEqual(other.RequiredTrackTitles)));
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(MinTrackCount);
            hash.Add(MaxTrackCount);

            if (RequiredTrackTitles != null)
                foreach (var title in RequiredTrackTitles)
                    hash.Add(title);

            return hash.ToHashCode();
        }

        public void AddRequiredTrackTitle(string title)
        {
            if (title.Length == 0)
                return;

            RequiredTrackTitles ??= [];
            if (!RequiredTrackTitles.Contains(title))
                RequiredTrackTitles.Add(title);
        }

        public void AddRequiredTrackTitles(IEnumerable<string> titles)
        {
            foreach (var title in titles)
                AddRequiredTrackTitle(title);
        }
    }
