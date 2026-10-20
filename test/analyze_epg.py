
import re
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "resurses"
M3U = ROOT / "playlist.m3u8"
XML = ROOT / "epg2.xml"


def read_m3u():
    channels = []

    for line in M3U.read_text(encoding="utf-8-sig").splitlines():
        if line.startswith("#EXTINF:") and "," in line:
            name = line.split(",", 1)[1].strip()
            channels.append(name)

    return channels


def read_epg():
    channels = []

    for event, elem in ET.iterparse(XML, events=("end",)):
        if elem.tag == "channel":
            names = [
                node.text.strip()
                for node in elem.findall("display-name")
                if node.text and node.text.strip()
            ]

            channels.append({
                "id": elem.get("id"),
                "names": names
            })

            elem.clear()

    return channels


def main():
    print("Читаем M3U...")
    m3u = read_m3u()

    print("Читаем EPG...")
    epg = read_epg()

    epg_by_name = {}

    for channel in epg:
        for name in channel["names"]:
            key = name.casefold()
            epg_by_name.setdefault(key, set()).add(channel["id"])

    exact = []
    ambiguous = []
    missing = []

    for name in m3u:
        ids = epg_by_name.get(name.casefold(), set())

        if len(ids) == 1:
            exact.append((name, next(iter(ids))))
        elif len(ids) > 1:
            ambiguous.append(name)
        else:
            missing.append(name)

    print("\n=== РЕЗУЛЬТАТ ===")
    print(f"M3U каналов:         {len(m3u)}")
    print(f"EPG каналов:         {len(epg)}")
    print(f"Точных совпадений:   {len(exact)}")
    print(f"Неоднозначных:       {len(ambiguous)}")
    print(f"Без совпадений:      {len(missing)}")
    
    unique_ids = {channel_id for name, channel_id in exact}

    print("\n=== УНИКАЛЬНЫЕ EPG ID ===")
    print(f"Совпавших M3U каналов: {len(exact)}")
    print(f"Уникальных EPG ID:     {len(unique_ids)}")
    print(f"Повторных привязок:     {len(exact) - len(unique_ids)}")

    print("\nПервые 15 точных совпадений:")
    for name, channel_id in exact[:15]:
        print(f"  {name} -> {channel_id}")
    
    print("\n=== НЕОДНОЗНАЧНЫЕ КАНАЛЫ ===")

    for name in ambiguous:
        ids = epg_by_name.get(name.casefold(), set())
        print(f"  {name} -> {', '.join(sorted(ids))}")

    print("\nПервые 15 без совпадений:")
    for name in missing[:15]:
        print(f"  {name}")


if __name__ == "__main__":
    main()
