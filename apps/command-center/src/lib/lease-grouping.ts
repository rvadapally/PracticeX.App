import type { PortfolioDocument } from './api';

export interface LeaseGroup {
  key: string;
  displayAddress: string;
  current: PortfolioDocument;
  history: PortfolioDocument[]; // older versions, descending by date
  activeCount: number;
  expiredCount: number;
}

// Normalize an address into a stable grouping key. Different documents about
// the same building word the address differently — "1002 N. Church Street",
// "1002 North Church Street, NC 27401", "Suite 201, 1002 North Church
// Street" — so we strip leading suite/unit prefixes, drop direction words,
// drop street suffixes, drop the state and ZIP, and collapse whitespace.
export function normalizePropertyKey(address: string | null | undefined): string | null {
  if (!address) return null;
  let s = address.toLowerCase().trim();

  // Strip leading "Suite #2," / "Suite 201," / "Unit B," prefixes.
  s = s.replace(/^\s*(suite|ste|unit|apt|apartment|#)\s*[#a-z0-9-]+\s*[,-]\s*/i, '');

  // Remove ZIP codes (5-digit, optional -4).
  s = s.replace(/\b\d{5}(-\d{4})?\b/g, '');

  // Drop state forms — both abbreviations after a comma and full state names.
  s = s.replace(/,\s*(north carolina|south carolina|nc|sc|n\.c\.|s\.c\.)\b\.?/g, '');

  // Tokenize, drop direction words and common street suffixes that vary
  // between renderings of the same address.
  const STOPWORDS = new Set([
    'n', 'n.', 'north',
    's', 's.', 'south',
    'e', 'e.', 'east',
    'w', 'w.', 'west',
    'ne', 'nw', 'se', 'sw',
    'st', 'st.', 'street',
    'rd', 'rd.', 'road',
    'ave', 'ave.', 'avenue',
    'blvd', 'blvd.', 'boulevard',
    'dr', 'dr.', 'drive',
    'ln', 'ln.', 'lane',
    'pl', 'pl.', 'place',
    'ct', 'ct.', 'court',
    'pkwy', 'parkway',
    'hwy', 'highway',
  ]);
  const tokens = s
    .replace(/[.,;]/g, ' ')
    .split(/\s+/)
    .map((t) => t.trim())
    .filter((t) => t.length > 0 && !STOPWORDS.has(t));

  return tokens.join(' ');
}

// Pick the cleanest variant of an address for display — prefer one that
// includes the state, doesn't start with a unit prefix, and isn't excessively
// long. Falls back to the first variant if none stand out.
function pickCanonicalAddress(addresses: string[]): string {
  const candidates = addresses.filter(Boolean);
  if (candidates.length === 0) return '';
  // De-prioritize variants that lead with a Suite/Unit prefix.
  const ranked = [...candidates].sort((a, b) => {
    const aSuite = /^\s*(suite|ste|unit)/i.test(a) ? 1 : 0;
    const bSuite = /^\s*(suite|ste|unit)/i.test(b) ? 1 : 0;
    if (aSuite !== bSuite) return aSuite - bSuite;
    // Prefer ones that include a state/ZIP signal.
    const aState = /,\s*(nc|sc|north carolina|south carolina|\d{5})/i.test(a) ? 0 : 1;
    const bState = /,\s*(nc|sc|north carolina|south carolina|\d{5})/i.test(b) ? 0 : 1;
    if (aState !== bState) return aState - bState;
    return a.length - b.length;
  });
  return ranked[0];
}

function effectiveTime(d: PortfolioDocument): number {
  if (d.effectiveDate) {
    const t = Date.parse(d.effectiveDate);
    if (!Number.isNaN(t)) return t;
  }
  return Date.parse(d.createdAt) || 0;
}

// Group lease-family documents by normalized property address. Documents
// without a readable propertyAddress fall through into per-document groups
// (so they still render — just as singletons).
export function groupLeasesByProperty(docs: PortfolioDocument[]): LeaseGroup[] {
  const buckets = new Map<string, { addresses: string[]; docs: PortfolioDocument[] }>();

  for (const doc of docs) {
    const key = normalizePropertyKey(doc.propertyAddress) ?? `__solo:${doc.documentAssetId}`;
    const bucket = buckets.get(key);
    if (bucket) {
      bucket.docs.push(doc);
      if (doc.propertyAddress) bucket.addresses.push(doc.propertyAddress);
    } else {
      buckets.set(key, {
        addresses: doc.propertyAddress ? [doc.propertyAddress] : [],
        docs: [doc],
      });
    }
  }

  const groups: LeaseGroup[] = [];
  for (const [key, bucket] of buckets) {
    const sorted = [...bucket.docs].sort((a, b) => effectiveTime(b) - effectiveTime(a));
    const [current, ...history] = sorted;
    groups.push({
      key,
      displayAddress: pickCanonicalAddress(bucket.addresses) || current.fileName,
      current,
      history,
      activeCount: sorted.filter((d) => d.expirationStatus === 'active').length,
      expiredCount: sorted.filter((d) => d.expirationStatus === 'expired').length,
    });
  }

  // Largest groups first, then most-recent activity.
  groups.sort((a, b) => {
    const sizeDelta = b.history.length + 1 - (a.history.length + 1);
    if (sizeDelta !== 0) return sizeDelta;
    return effectiveTime(b.current) - effectiveTime(a.current);
  });
  return groups;
}
