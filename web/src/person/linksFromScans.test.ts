import { describe, expect, it } from 'vitest';
import type { Assignment } from '../api/client';
import type { LibraryScan, ScannedItem } from '../db/scans';
import { linksFromScans, tallyScopes } from './linksFromScans';

function link(overrides: Partial<Assignment> = {}): Assignment {
  return {
    principalId: 0,
    principalName: 'Anyone with the link',
    principalType: 0,
    loginName: '',
    roles: ['read'],
    source: 'Sharing link',
    linkScope: 'organization',
    expiry: null,
    ...overrides,
  };
}

function item(overrides: Partial<ScannedItem> = {}): ScannedItem {
  return {
    id: 1,
    path: '/sites/x/Documents/a.txt',
    name: 'a.txt',
    isFolder: false,
    broken: true,
    assignments: [],
    ...overrides,
  };
}

function scan(items: ScannedItem[], listTitle = 'Documents'): LibraryScan {
  return {
    siteId: 'site',
    listId: 'list',
    listTitle,
    savedAt: '2026-09-05T00:00:00Z',
    itemCount: items.length,
    items,
    permissionRoots: items.filter((i) => i.broken).map((i) => i.id),
    libraryIsPermissionRoot: false,
  };
}

describe('linksFromScans', () => {
  it('finds a sharing link the library scan already read', () => {
    const links = linksFromScans([scan([item({ assignments: [link()] })])]);

    expect(links).toHaveLength(1);
    expect(links[0]?.path).toBe('/sites/x/Documents/a.txt');
    expect(links[0]?.library).toBe('Documents');
    expect(links[0]?.scope).toBe('organization');
  });

  it('ignores direct grants', () => {
    const grant = link({ source: 'Direct grant', linkScope: null });

    expect(linksFromScans([scan([item({ assignments: [grant] })])])).toHaveLength(0);
  });

  it('ignores items that inherit, which carry no assignments of their own', () => {
    expect(
      linksFromScans([scan([item({ broken: false, assignments: [link()] })])]),
    ).toHaveLength(0);
  });

  it('finds several links on one item', () => {
    const links = linksFromScans([
      scan([item({ assignments: [link(), link({ linkScope: 'anonymous' })] })]),
    ]);

    expect(links).toHaveLength(2);
  });

  it('treats an anonymous link as external whoever holds it', () => {
    const links = linksFromScans([scan([item({ assignments: [link({ linkScope: 'anonymous' })] })])]);

    expect(links[0]?.external).toBe(true);
  });

  it('recognises a guest recipient by the claim in the login name', () => {
    const guest = link({
      linkScope: 'users',
      principalName: 'Outside Person',
      loginName: 'i:0#.f|membership|out_partner.com#ext#@contoso.onmicrosoft.com',
    });

    expect(linksFromScans([scan([item({ assignments: [guest] })])])[0]?.external).toBe(true);
  });

  it('does not mark an internal organisation link as external', () => {
    expect(linksFromScans([scan([item({ assignments: [link()] })])])[0]?.external).toBe(false);
  });

  it('spans several libraries', () => {
    const links = linksFromScans([
      scan([item({ assignments: [link()] })], 'Documents'),
      scan([item({ id: 2, assignments: [link()] })], 'SmallLib'),
    ]);

    expect(links.map((l) => l.library)).toEqual(['Documents', 'SmallLib']);
  });
});

describe('tallyScopes', () => {
  it('counts each scope, and the counts sum to the links found', () => {
    const links = linksFromScans([
      scan([
        item({ assignments: [link({ linkScope: 'anonymous' })] }),
        item({ id: 2, assignments: [link({ linkScope: 'organization' })] }),
        item({ id: 3, assignments: [link({ linkScope: 'users' })] }),
        item({ id: 4, assignments: [link({ linkScope: 'existingAccess' })] }),
      ]),
    ]);

    const counts = tallyScopes(links);

    expect(counts).toEqual({ anyone: 1, organization: 1, specific: 1, other: 1 });
    expect(counts.anyone + counts.organization + counts.specific + counts.other).toBe(links.length);
  });
});
