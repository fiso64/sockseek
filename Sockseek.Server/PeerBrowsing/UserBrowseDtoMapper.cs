using System.Globalization;
using Sockseek.Api;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;

namespace Sockseek.Server.PeerBrowsing;

internal static class UserBrowseDtoMapper
{
    public static UserBrowseDto ToDto(PeerBrowseResource resource)
        => new(
            resource.BrowseId,
            resource.Username,
            (UserBrowseState)resource.State,
            (UserBrowsePhase)resource.Phase,
            resource.CompressedBytesReceived,
            resource.CompressedBytesExpected,
            resource.DirectoryCount,
            resource.FileCount,
            resource.TotalFileBytes,
            resource.CreatedAt,
            resource.UpdatedAt,
            resource.State is PeerBrowseState.Queued or PeerBrowseState.Running
                ? null
                : resource.ExpiresAt,
            resource.Failure is null ? null : new ApiErrorDto(resource.Failure.Message, resource.Failure.Code),
            resource.Revision);

    public static BrowseDirectoryEntryDto ToDto(PeerBrowseDirectoryEntry entry)
        => new(
            entry.DirectoryId,
            entry.ParentId,
            PeerIdentityValidator.ToDisplayText(entry.Name),
            PeerIdentityValidator.ToDisplayText(entry.DisplayPath),
            (ShareVisibility)entry.Visibility,
            entry.IsSynthetic,
            entry.DirectDirectoryCount,
            entry.DirectFileCount,
            entry.RecursiveFileCount,
            entry.RecursiveFileBytes,
            entry.LockedDescendantCount,
            entry.HasChildren,
            entry.TotalRecursiveFileCount,
            entry.TotalRecursiveFileBytes);

    public static BrowseFileEntryDto ToDto(PeerBrowseFileEntry entry)
        => new(
            entry.FileId,
            entry.DirectoryId,
            (ShareVisibility)entry.Visibility,
            new FileMetadataDto(
                PeerIdentityValidator.ToDisplayText(entry.Name),
                entry.Size,
                entry.Extension is null ? null : PeerIdentityValidator.ToDisplayText(entry.Extension),
                entry.BitRate,
                entry.BitDepth,
                entry.SampleRate,
                entry.Length,
                entry.Attributes?.Select(static attribute => new FileAttributeDto(
                    AttributeType(attribute.Type), attribute.Value)).ToArray()));

    public static BrowseSearchEntryDto ToDto(PeerBrowseSearchEntry entry)
        => new(
            (BrowseSearchEntryKind)entry.Kind,
            entry.EntryId,
            entry.DirectoryId,
            entry.ParentDirectoryId,
            PeerIdentityValidator.ToDisplayText(entry.Name),
            PeerIdentityValidator.ToDisplayText(entry.DisplayPath),
            (ShareVisibility)entry.Visibility,
            entry.PublicMatchingFileCount,
            entry.PublicMatchingBytes,
            entry.LockedMatchingFileCount,
            entry.LockedMatchingBytes,
            entry.FileSize,
            entry.Extension is null ? null : PeerIdentityValidator.ToDisplayText(entry.Extension),
            entry.BitRate,
            entry.BitDepth,
            entry.SampleRate,
            entry.Length,
            entry.PublicSubtreeFileCount,
            entry.PublicSubtreeBytes,
            entry.TotalSubtreeFileCount,
            entry.TotalSubtreeBytes);

    private static string AttributeType(int type)
        => type switch
        {
            0 => "BitRate",
            1 => "Length",
            2 => "VariableBitRate",
            4 => "SampleRate",
            5 => "BitDepth",
            _ => type.ToString(CultureInfo.InvariantCulture),
        };
}
