"""A small seeded generator of numbers (SplitMix64) with the draws the case generator needs.

Python's own ``random`` would do on one interpreter, but its methods are not a documented cross-version contract. This
one is: 64-bit integer arithmetic only, and every float is a 53-bit fraction of an integer, so the same seed gives the
same sequence on every platform and Python version. Every value drawn for a case is then rounded to a fixed number of
decimals before it is written, so the case text is the same bytes everywhere.
"""

import hashlib

_MASK = (1 << 64) - 1


def derive(*parts):
    """A 64-bit seed from any parts (a campaign seed, a case index, a family name): the first 8 bytes of a SHA-256."""
    text = "\x1f".join(str(p) for p in parts)
    return int.from_bytes(hashlib.sha256(text.encode("utf-8")).digest()[:8], "big")


class Rng:
    def __init__(self, seed):
        self.state = int(seed) & _MASK

    def next_u64(self):
        self.state = (self.state + 0x9E3779B97F4A7C15) & _MASK
        z = self.state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & _MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & _MASK
        return z ^ (z >> 31)

    def random(self):
        """A float in [0, 1)."""
        return (self.next_u64() >> 11) / float(1 << 53)

    def uniform(self, lo, hi, nd=6):
        """A float in [lo, hi], rounded to nd decimals."""
        return round(lo + (hi - lo) * self.random(), nd)

    def randint(self, lo, hi):
        """An integer in [lo, hi] (no modulo bias)."""
        span = hi - lo + 1
        if span <= 0:
            raise ValueError("empty range")
        limit = (1 << 64) - ((1 << 64) % span)
        while True:
            v = self.next_u64()
            if v < limit:
                return lo + v % span

    def choice(self, items):
        items = list(items)
        return items[self.randint(0, len(items) - 1)]

    def chance(self, p):
        return self.random() < p

    def sample(self, items, k):
        """k distinct items, in draw order (a partial Fisher-Yates shuffle)."""
        pool = list(items)
        out = []
        for _ in range(min(k, len(pool))):
            out.append(pool.pop(self.randint(0, len(pool) - 1)))
        return out
