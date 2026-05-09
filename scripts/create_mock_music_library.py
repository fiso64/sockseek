#!/usr/bin/env python3
"""Create a fake local music library for --mock-files-dir testing.

The generated files are not valid FLAC audio, but they have .flac extensions and
real file sizes. Use with --mock-files-no-read-tags.
"""

from __future__ import annotations

import argparse
import csv
import random
import re
from pathlib import Path


ALBUMS: list[tuple[str, str]] = [
    ("Radiohead", "In Rainbows"),
    ("Daft Punk", "Discovery"),
    ("Kendrick Lamar", "To Pimp a Butterfly"),
    ("Fleetwood Mac", "Rumours"),
    ("Nirvana", "Nevermind"),
    ("The Beatles", "Abbey Road"),
    ("Pink Floyd", "The Dark Side of the Moon"),
    ("Stevie Wonder", "Songs in the Key of Life"),
    ("Joni Mitchell", "Blue"),
    ("Miles Davis", "Kind of Blue"),
    ("The Strokes", "Is This It"),
    ("Arcade Fire", "Funeral"),
    ("Portishead", "Dummy"),
    ("Massive Attack", "Mezzanine"),
    ("Bjork", "Homogenic"),
    ("Aphex Twin", "Selected Ambient Works 85-92"),
    ("The Cure", "Disintegration"),
    ("Prince", "Purple Rain"),
    ("Kate Bush", "Hounds of Love"),
    ("David Bowie", "Low"),
    ("Lauryn Hill", "The Miseducation of Lauryn Hill"),
    ("OutKast", "Aquemini"),
    ("A Tribe Called Quest", "The Low End Theory"),
    ("Public Enemy", "It Takes a Nation of Millions"),
    ("Nas", "Illmatic"),
    ("Wu-Tang Clan", "Enter the Wu-Tang"),
    ("The Clash", "London Calling"),
    ("Television", "Marquee Moon"),
    ("Joy Division", "Unknown Pleasures"),
    ("Talking Heads", "Remain in Light"),
    ("Sonic Youth", "Daydream Nation"),
    ("My Bloody Valentine", "Loveless"),
    ("Neutral Milk Hotel", "In the Aeroplane Over the Sea"),
    ("Elliott Smith", "Either Or"),
    ("Sufjan Stevens", "Illinois"),
    ("Bon Iver", "For Emma Forever Ago"),
    ("Frank Ocean", "Blonde"),
    ("FKA twigs", "LP1"),
    ("LCD Soundsystem", "Sound of Silver"),
    ("The Avalanches", "Since I Left You"),
    ("Burial", "Untrue"),
    ("Boards of Canada", "Music Has the Right to Children"),
    ("Tame Impala", "Currents"),
    ("Gorillaz", "Demon Days"),
    ("The National", "Boxer"),
    ("PJ Harvey", "Stories from the City Stories from the Sea"),
    ("Mitski", "Be the Cowboy"),
    ("Phoebe Bridgers", "Punisher"),
    ("The War on Drugs", "Lost in the Dream"),
    ("Wilco", "Yankee Hotel Foxtrot"),
]

TITLE_WORDS = [
    "Midnight",
    "Signal",
    "Golden",
    "Static",
    "River",
    "Mirror",
    "City",
    "Garden",
    "Neon",
    "Summer",
    "Winter",
    "Velvet",
    "Satellite",
    "Memory",
    "Dream",
    "Ocean",
    "Street",
    "Horizon",
    "Fever",
    "Echo",
    "Light",
    "Shadow",
    "Palace",
    "Weather",
    "Morning",
    "Night",
    "Glass",
    "Fire",
    "Paper",
    "Silver",
]


def safe_path_part(value: str) -> str:
    value = re.sub(r'[<>:"/\\|?*]', "", value)
    value = re.sub(r"\s+", " ", value).strip()
    return value.rstrip(". ")


def make_track_title(rng: random.Random, used: set[str]) -> str:
    while True:
        words = rng.sample(TITLE_WORDS, rng.randint(2, 4))
        title = " ".join(words)
        if title not in used:
            used.add(title)
            return title


def create_fake_file(path: Path, size_bytes: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as handle:
        handle.truncate(size_bytes)


def generate(root: Path, seed: int) -> None:
    rng = random.Random(seed)
    library_dir = root / "mock-library"
    csv_dir = root / "csv"
    csv_dir.mkdir(parents=True, exist_ok=True)

    track_rows: list[dict[str, str]] = []
    album_rows: list[dict[str, str]] = []

    for artist, album in ALBUMS:
        track_count = rng.randint(8, 12)
        album_rows.append({"artist": artist, "title": "", "album": album})
        used_titles: set[str] = set()

        for track_number in range(1, track_count + 1):
            title = make_track_title(rng, used_titles)
            size_bytes = rng.randint(1, 5) * 1024 * 1024 + rng.randint(0, 1023)
            filename = f"{track_number:02d}. {artist} - {title}.flac"
            path = (
                library_dir
                / safe_path_part(artist)
                / safe_path_part(album)
                / safe_path_part(filename)
            )
            create_fake_file(path, size_bytes)

            track_rows.append({
                "artist": artist,
                "title": title,
                "album": album,
            })

    selected_tracks = rng.sample(track_rows, 100)
    selected_albums = rng.sample(album_rows, 30)

    list_lines: list[str] =[]
    sample_albums = rng.sample(album_rows, 25)
    sample_tracks = rng.sample(track_rows, 25)
    
    for album in sample_albums:
        list_lines.append(f'a:"{album["artist"]} - {album["album"]}"')
    for track in sample_tracks:
        list_lines.append(f'"{track["artist"]} - {track["title"]}"')
        
    rng.shuffle(list_lines)

    tracks_csv = csv_dir / "tracks_to_download.csv"
    albums_csv = csv_dir / "albums_to_download.csv"
    list_txt = csv_dir / "list.txt"

    with tracks_csv.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=["artist", "title", "album"])
        writer.writeheader()
        writer.writerows(selected_tracks)

    with albums_csv.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=["artist", "title", "album"])
        writer.writeheader()
        writer.writerows(selected_albums)

    with list_txt.open("w", encoding="utf-8") as handle:
        for line in list_lines:
            handle.write(line + "\n")

    print(f"Created library: {library_dir}")
    print(f"Created tracks CSV: {tracks_csv}")
    print(f"Created albums CSV: {albums_csv}")
    print(f"Created list.txt: {list_txt}")
    print()
    print("Example commands:")
    print(f'  sldl "{tracks_csv}" --mock-files-dir "{library_dir}" --mock-files-no-read-tags --mock-files-slow')
    print(f'  sldl "{albums_csv}" --mock-files-dir "{library_dir}" --mock-files-no-read-tags --mock-files-slow')
    print(f'  sldl "{list_txt}" --input-type list --mock-files-dir "{library_dir}" --mock-files-no-read-tags --mock-files-slow')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        required=True,
        help="Output directory for the generated library and CSV files.",
    )
    parser.add_argument(
        "--seed",
        type=int,
        default=55,
        help="Random seed for deterministic fixture generation.",
    )
    args = parser.parse_args()

    generate(args.output, args.seed)


if __name__ == "__main__":
    main()
