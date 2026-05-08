using Sldl.Core.Models;

namespace Sldl.Core.Services;

/// Parses semicolon-separated condition strings into FileConditionPatch and FolderConditionPatch.
/// Extracted from Config.ParseConditions — callers are List.cs extractor and ConfigManager.
public static class ConditionParser
{
    /// Parses a condition string (e.g. "format=mp3,flac;min-bitrate=200;album-track-count>=8")
    /// into a FileConditionPatch. Folder-level conditions (album-track-count) are written into
    /// folderOut if non-null; otherwise they produce an error.
    public static FileConditionPatch ParseFileConditions(string input, FolderConditionPatch? folderOut = null)
    {
        static void UpdateMinMax(string value, string condition, ref int? min, ref int? max)
        {
            if (condition.Contains(">="))
                min = int.Parse(value);
            else if (condition.Contains("<="))
                max = int.Parse(value);
            else if (condition.Contains('>'))
                min = int.Parse(value) + 1;
            else if (condition.Contains('<'))
                max = int.Parse(value) - 1;
            else if (condition.Contains('='))
                min = max = int.Parse(value);
        }

        var cond = new FileConditionPatch();

        var tr = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;
        string[] conditions = input.Split(';', tr);
        foreach (string condition in conditions)
        {
            string[] parts = condition.Split(new string[] { ">=", "<=", "=", ">", "<" }, 2, tr);
            string field = parts[0].Replace("-", "").Trim().ToLower();
            string value = parts.Length > 1 ? parts[1].Trim() : "true";

            switch (field)
            {
                case "sr":
                case "samplerate":
                    UpdateMinMax(value, condition, ref cond.MinSampleRate, ref cond.MaxSampleRate);
                    break;
                case "br":
                case "bitrate":
                    UpdateMinMax(value, condition, ref cond.MinBitrate, ref cond.MaxBitrate);
                    break;
                case "bd":
                case "bitdepth":
                    UpdateMinMax(value, condition, ref cond.MinBitDepth, ref cond.MaxBitDepth);
                    break;
                case "t":
                case "tol":
                case "lentol":
                case "lengthtol":
                case "tolerance":
                case "lengthtolerance":
                    cond.LengthTolerance = int.Parse(value);
                    break;
                case "f":
                case "format":
                case "formats":
                    cond.Formats = value.Split(',', tr).Select(x => x.TrimStart('.')).ToArray();
                    break;
                case "banned":
                case "bannedusers":
                    cond.BannedUsers = value.Split(',', tr);
                    break;
                case "allowed":
                case "allowedusers":
                    cond.AllowedUsers = value.Split(',', tr);
                    break;
                case "stricttitle":
                    cond.StrictTitle = bool.Parse(value);
                    break;
                case "strictartist":
                    cond.StrictArtist = bool.Parse(value);
                    break;
                case "strictalbum":
                    cond.StrictAlbum = bool.Parse(value);
                    break;
                case "acceptnolen":
                case "acceptnolength":
                    cond.AcceptNoLength = bool.Parse(value);
                    break;
                case "strict":
                case "strictconditions":
                case "acceptmissing":
                case "acceptmissingprops":
                    cond.AcceptMissingProps = bool.Parse(value);
                    break;
                case "albumtrackcount":
                    if (folderOut != null)
                    {
                        int? minC = folderOut.MinTrackCount, maxC = folderOut.MaxTrackCount;
                        UpdateMinMax(value, condition, ref minC, ref maxC);
                        folderOut.MinTrackCount = minC;
                        folderOut.MaxTrackCount = maxC;
                    }
                    else
                    {
                        throw new Exception($"Input error: '{condition}' is a folder condition and has no effect here");
                    }
                    break;
                case "requiredtracktitle":
                case "foldertracktitle":
                case "tracktitle":
                    if (folderOut != null)
                    {
                        folderOut.AddRequiredTrackTitle(value);
                    }
                    else
                    {
                        throw new Exception($"Input error: '{condition}' is a folder condition and has no effect here");
                    }
                    break;
                default:
                    throw new Exception($"Input error: Unknown condition '{condition}'");
            }
        }

        return cond;
    }

    /// Convenience: parse only folder-level conditions from a condition string.
    /// File-level conditions in the string are parsed and discarded.
    public static FolderConditionPatch ParseFolderConditions(string input)
    {
        var folderOut = new FolderConditionPatch();
        ParseFileConditions(input, folderOut);
        return folderOut;
    }
}
