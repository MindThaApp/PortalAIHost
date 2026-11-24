// Just enough DER to pull the SubjectPublicKeyInfo out of an X.509 certificate.

interface Tlv {
  tag: number;
  start: number; // first byte of the whole element
  body: number; // first byte of the content
  end: number; // one past the last byte
}

function readTlv(buf: Uint8Array, off: number, limit: number): Tlv | null {
  if (off + 2 > limit) return null;
  const tag = buf[off]!;
  let len = buf[off + 1]!;
  let p = off + 2;
  if (len & 0x80) {
    const n = len & 0x7f;
    if (n < 1 || n > 3 || p + n > limit) return null;
    len = 0;
    for (let i = 0; i < n; i++) len = (len << 8) | buf[p++]!;
  }
  const end = p + len;
  return end > limit ? null : { tag, start: off, body: p, end };
}

/** Certificate ::= SEQUENCE { tbsCertificate SEQUENCE { [0] version?, serial, sigAlg, issuer, validity, subject, spki, ... }, ... } */
export function spkiFromCertificate(der: Uint8Array): Uint8Array | null {
  const cert = readTlv(der, 0, der.length);
  if (!cert || cert.tag !== 0x30 || cert.end !== der.length) return null;
  const tbs = readTlv(der, cert.body, cert.end);
  if (!tbs || tbs.tag !== 0x30) return null;
  let off = tbs.body;
  let el = readTlv(der, off, tbs.end);
  if (el && el.tag === 0xa0) {
    off = el.end; // version
  }
  // serial, signature algorithm, issuer, validity, subject
  for (let i = 0; i < 5; i++) {
    el = readTlv(der, off, tbs.end);
    if (!el) return null;
    off = el.end;
  }
  const spki = readTlv(der, off, tbs.end);
  if (!spki || spki.tag !== 0x30) return null;
  return der.slice(spki.start, spki.end);
}
