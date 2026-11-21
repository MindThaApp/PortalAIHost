"""Writes a pseudo-localised copy of a .resw file: accented letters, +33% length, {n} placeholders kept."""
import os
import sys
import xml.etree.ElementTree as ET

SRC = "AEIOUaeiouCcNnSsYyZz"
DST = "ÀÉÎÕÛàéîõûÇçÑñŠšÝýŽž"


def pseudo(text):
    out = []
    depth = 0
    for ch in text:
        if ch == "{":
            depth += 1
        i = -1 if depth else SRC.find(ch)
        out.append(DST[i] if i >= 0 else ch)
        if ch == "}":
            depth = max(0, depth - 1)
    return "[" + "".join(out) + "~" * max(1, len(text) // 3) + "]"


def main(src, dst):
    tree = ET.parse(src)
    for data in tree.getroot().findall("data"):
        value = data.find("value")
        if value is not None and value.text:
            value.text = pseudo(value.text)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    tree.write(dst, encoding="utf-8", xml_declaration=True)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
