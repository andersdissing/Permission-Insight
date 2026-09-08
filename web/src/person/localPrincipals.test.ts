import { describe, expect, it } from 'vitest';
import type { Assignment } from '../api/client';
import type { LibraryScan, ScannedItem } from '../db/scans';
import { kindGlyph, kindLabel, sharePointGroupsFromScans, SHAREPOINT_GROUP } from './localPrincipals';

function assignment(overrides: Partial<Assignment> = {}): Assignment {
  return {
    principalId: 0,
    principalName: 'PI Nested Access',
    principalType: 8,
    loginName: 'PI Nested Access',
    roles: ['read'],
    source: 'Direct grant',
    ...overrides,
  };
}

function scan(items: ScannedItem[]): LibraryScan {
  return {
    siteId: 'site',
    listId: 'list',
    listTitle: 'Documents',
    savedAt: '2026-09-07T00:00:00Z',
    itemCount: items.length,
    items,
    permissionRoots: [],
    libraryIsPermissionRoot: false,
  };
}

function item(assignments: Assignment[], id = 1): ScannedItem {
  return { id, path: `/x/${id}`, name: `${id}`, isFolder: false, broken: true, assignments };
}

describe('sharePointGroupsFromScans', () => {
  it('finds a SharePoint group by part of its name', () => {
    const results = sharePointGroupsFromScans([scan([item([assignment()])])], 'nested');

    expect(results).toHaveLength(1);
    expect(results[0]?.displayName).toBe('PI Nested Access');
    expect(results[0]?.kind).toBe(SHAREPOINT_GROUP);
  });

  it('ignores users and Entra groups, which Graph searches properly', () => {
    const others = [
      assignment({ principalType: 1, principalName: 'Nested Person' }),
      assignment({ principalType: 4, principalName: 'sg-nested' }),
    ];

    expect(sharePointGroupsFromScans([scan([item(others)])], 'nested')).toHaveLength(0);
  });

  it('reports a group once however many items name it', () => {
    const group = assignment({ principalRef: '7' });
    const scans = [scan([item([group], 1), item([group], 2), item([group], 3)])];

    expect(sharePointGroupsFromScans(scans, 'nested')).toHaveLength(1);
  });

  it('keeps two groups that share no identifier apart', () => {
    const scans = [
      scan([
        item([assignment({ principalName: 'Nested One', principalRef: '1' })], 1),
        item([assignment({ principalName: 'Nested Two', principalRef: '2' })], 2),
      ]),
    ];

    expect(sharePointGroupsFromScans(scans, 'nested')).toHaveLength(2);
  });

  it('matches case-insensitively', () => {
    expect(sharePointGroupsFromScans([scan([item([assignment()])])], 'NESTED')).toHaveLength(1);
  });

  it('needs at least two characters, like the directory search', () => {
    expect(sharePointGroupsFromScans([scan([item([assignment()])])], 'n')).toHaveLength(0);
  });

  it('returns nothing when nothing has been scanned', () => {
    expect(sharePointGroupsFromScans([], 'nested')).toHaveLength(0);
  });
});

describe('kindLabel', () => {
  it.each([
    ['user', 'User'],
    ['group', 'Entra ID group'],
    [SHAREPOINT_GROUP, 'SharePoint group'],
  ])('%s reads as %s', (kind, expected) => {
    expect(kindLabel(kind)).toBe(expected);
  });
});

describe('kindGlyph', () => {
  it('gives users no glyph, so their initials show instead', () => {
    expect(kindGlyph('user')).toBeNull();
  });

  it('distinguishes the two kinds of group', () => {
    expect(kindGlyph('group')).not.toBe(kindGlyph(SHAREPOINT_GROUP));
  });
});
