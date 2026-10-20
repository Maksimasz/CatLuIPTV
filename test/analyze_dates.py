
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

xml_path = Path(__file__).resolve().parent.parent / "resurses" / "epg2.xml"

today = datetime.now(timezone.utc).date()

dates = Counter()
total = 0
invalid = 0

print("Анализируем даты передач...")

for event, elem in ET.iterparse(xml_path, events=("end",)):
    if elem.tag == "programme":
        total += 1

        start = elem.get("start", "")

        try:
            # XMLTV: 20261020143000 +0300
            dt = datetime.strptime(start[:14], "%Y%m%d%H%M%S")

            if len(start) >= 20:
                offset = start[15:20]
                dt = datetime.strptime(
                    start[:14] + offset,
                    "%Y%m%d%H%M%S%z"
                ).astimezone(timezone.utc)

            dates[dt.date()] += 1
        except (ValueError, IndexError):
            invalid += 1

        elem.clear()
    elif elem.tag == "channel":
        elem.clear()

print("\n=== ДАТЫ EPG (UTC) ===")

for day, count in sorted(dates.items()):
    print(f"{day}: {count:,} передач")

print("\n=== ИТОГ ===")
print(f"Всего передач:       {total:,}")
print(f"Дней в EPG:          {len(dates)}")
print(f"Прошедшие дни:       {sum(n for d, n in dates.items() if d < today):,}")
print(f"Сегодня и будущие:   {sum(n for d, n in dates.items() if d >= today):,}")
print(f"Ошибки дат:          {invalid:,}")
