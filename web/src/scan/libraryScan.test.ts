import { describe, expect, it } from 'vitest';
import { isEmptyList, isSuspiciouslyIncomplete, progressText, sizeBadge } from './libraryScan';

describe('isSuspiciouslyIncomplete', () => {
  it('catches paging that stopped after the first page', () => {
    // The real bug: a 12,000 item library reported exactly 1,000, and every
    // finding past the first page was invisible.
    expect(isSuspiciouslyIncomplete(1000, 12000)).toBe(true);
  });

  it('accepts a scan that read everything', () => {
    expect(isSuspiciouslyIncomplete(12000, 12000)).toBe(false);
  });

  it('accepts more items than reported, because ItemCount can lag', () => {
    expect(isSuspiciouslyIncomplete(12010, 12000)).toBe(false);
  });

  it('tolerates a small shortfall, because items move while a scan runs', () => {
    expect(isSuspiciouslyIncomplete(11990, 12000)).toBe(false);
  });

  it('tolerates a proportionally large shortfall on a tiny list', () => {
    // Ten of twelve is 17% short, but two items is not evidence of anything.
    expect(isSuspiciouslyIncomplete(10, 12)).toBe(false);
  });

  it('needs both a wide gap and a large proportion', () => {
    // 5% short of 12,000 is 600 items, which is a lot in absolute terms but
    // well inside the churn a live library produces.
    expect(isSuspiciouslyIncomplete(11400, 12000)).toBe(false);
    expect(isSuspiciouslyIncomplete(10000, 12000)).toBe(true);
  });

  it('says nothing about an empty list', () => {
    expect(isSuspiciouslyIncomplete(0, 0)).toBe(false);
  });
});

describe('sizeBadge', () => {
  it.each([
    [1, 'Small', false, 'small'],
    [1999, 'Small', false, 'small'],
    [2000, 'Medium', false, 'medium'],
    [5999, 'Medium', false, 'medium'],
    [6000, 'Large', true, 'large'],
    [9999, 'Large', true, 'large'],
    [10000, 'Extra large', true, 'extra-large'],
    [12000, 'Extra large', true, 'extra-large'],
  ])('%i items is %s', (itemCount, label, warning, kind) => {
    // Boundaries are inclusive at the lower end. Off by one here mislabels a
    // library on every row it appears.
    expect(sizeBadge(itemCount)).toEqual({ label, warning, kind });
  });

  it('tints only the two largest sizes', () => {
    expect(sizeBadge(1999).warning).toBe(false);
    expect(sizeBadge(5999).warning).toBe(false);
    expect(sizeBadge(6000).warning).toBe(true);
  });

  it('calls a list with no items empty rather than small', () => {
    // It is not a small library. There is nothing in it, which is why it is
    // never scanned.
    expect(sizeBadge(0)).toEqual({ label: 'Empty', warning: false, kind: 'empty' });
  });

  it('keeps an unreported count separate from an empty one', () => {
    // The distinction the whole change rests on. Collapsing these would skip
    // a library that might hold thousands of items.
    expect(sizeBadge(null)).toEqual({ label: 'Size unknown', warning: false, kind: 'unknown' });
  });

  it('treats a missing count the same as a null one', () => {
    // A serialiser that omits nulls sends undefined. Falling through every
    // comparison would land on "Small", which is the one answer that cannot be
    // right about a count nobody reported.
    expect(sizeBadge(undefined)).toEqual({
      label: 'Size unknown',
      warning: false,
      kind: 'unknown',
    });
  });
});

describe('isEmptyList', () => {
  it('is true only for a list SharePoint said holds nothing', () => {
    expect(isEmptyList(0)).toBe(true);
    expect(isEmptyList(1)).toBe(false);
  });

  it('is false when the count is unknown, so the list is still scanned', () => {
    // "Nothing here" and "SharePoint didn't say" lead to opposite actions, and
    // only one of them is safe to guess. Guessing empty would silently drop a
    // library out of every scan and every export.
    expect(isEmptyList(null)).toBe(false);
    expect(isEmptyList(undefined)).toBe(false);
  });
});

describe('progressText', () => {
  it('names the step, the counts and the percentage while reading items', () => {
    expect(progressText({ step: 1, done: 7000, total: 12480, throttled: false })).toBe(
      'Step 1 of 2 · Reading items · 7,000 of 12,480 (56%)',
    );
  });

  it('drops the percentage in step two, where the denominator only just became known', () => {
    expect(progressText({ step: 2, done: 31, total: 84, throttled: false })).toBe(
      'Step 2 of 2 · Reading permissions · 31 of 84',
    );
  });

  it('says it is waiting rather than leaving a bar that looks stalled', () => {
    // A bar that has stopped moving with no explanation reads as broken. This
    // is the one case where the correct behaviour is to wait.
    expect(progressText({ step: 1, done: 3000, total: 12000, throttled: true })).toBe(
      'SharePoint is throttling. Waiting, then continuing',
    );
  });

  it('does not divide by zero on an empty library', () => {
    expect(progressText({ step: 1, done: 0, total: 0, throttled: false })).toContain('(0%)');
  });
});
