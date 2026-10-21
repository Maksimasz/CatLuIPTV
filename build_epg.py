
import gzip
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent / "resurses"
SOURCE = ROOT / "epg2.xml"
OUTPUT = ROOT / "epg_small.xml.gz"

today = datetime.now(timezone.utc).date()
start_date = today - timedelta(days=7)
end_date = today + timedelta(days=3)

print(f"Период: {start_date} — {end_date}")
print("Создаём облегчённый EPG...")

total = 0
saved = 0

with gzip.open(OUTPUT, "wt", encoding="utf-8", compresslevel=6) as out:
    out.write('<?xml version="1.0" encoding="UTF-8"?>\n')
    out.write('<tv generator-info-name="CatLu EPG">\n')

    for event, elem in ET.iterparse(SOURCE, events=("end",)):
        if elem.tag == "channel":
            out.write(ET.tostring(elem, encoding="unicode"))
            out.write("\n")
            elem.clear()

        elif elem.tag == "programme":
            total += 1

            start = elem.get("start", "")
            try:
                dt = datetime.strptime(start[:14], "%Y%m%d%H%M%S")
                if len(start) >= 20:
                    dt = datetime.strptime(
                        start[:14] + start[15:20],
                        "%Y%m%d%H%M%S%z"
                    ).astimezone(timezone.utc)

                if start_date <= dt.date() <= end_date:
                    out.write(ET.tostring(elem, encoding="unicode"))
                    out.write("\n")
                    saved += 1

            except ValueError:
                pass

            elem.clear()

    out.write("</tv>\n")

print(f"Всего передач: {total:,}")
print(f"Сохранено: {saved:,}")
print(f"Размер архива: {OUTPUT.stat().st_size / 1024 / 1024:.2f} MB")
