import { describe, expect, it } from 'vitest';
import type { Assignment, Person } from '../api/client';
import type { LibraryScan } from '../db/scans';
import { findAccess, matches } from './findAccess';

function person(overrides: Partial<Person> = {}): Person {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    displayName: 'Alpha Tester',
    mail: 'alpha@contoso.com',
    userPrincipalName: 'alpha@contoso.com',
    kind: 'user',
    ...overrides,
  };
}

function assignment(overrides: Partial<Assignment> = {}): Assignment {
  return {
    principalId: 0,
    principalName: 'Alpha Tester',
    principalType: 1,
    loginName: 'i:0#.f|membership|alpha@contoso.com',
    roles: ['write'],
    source: 'Direct grant',
    ...overrides,
  };
}

function scan(overrides: Partial<LibraryScan> = {}): LibraryScan {
  return {
    siteId: 'site',
    listId: 'list-1',
    listTitle: 'Documents',
    savedAt: '2026-09-04T00:00:00Z',
    itemCount: 3,
    items: [],
    permissionRoots: [],
    libraryIsPermissionRoot: false,
    ...overrides,
  };
}

describe('matches', () => {
  it('matches on the principal id, which is the strongest signal', () => {
    const target = person();
    expect(
      matches(assignment({ principalRef: target.id, loginName: '', principalName: 'Someone else' }), target),
    ).toBe(true);
  });

  it('matches on the address inside a claims login name', () => {
    expect(matches(assignment({ principalRef: null, principalName: 'Different' }), person())).toBe(true);
  });

  it('falls back to the display name', () => {
    expect(
      matches(assignment({ principalRef: null, loginName: '' }), person()),
    ).toBe(true);
  });

  it('does not match an unrelated principal', () => {
    expect(
      matches(assignment({ principalRef: null, loginName: 'i:0#.f|membership|someone@else.com', principalName: 'Someone Else' }), person()),
    ).toBe(false);
  });

  it('matches a group by its object id', () => {
    const group = person({ id: 'group-guid', displayName: 'sg-finance', kind: 'group', mail: '' });

    expect(matches(assignment({ principalRef: 'GROUP-GUID', principalType: 4 }), group)).toBe(true);
  });

  it('does not match on an empty address', () => {
    // A group with no mail must not match every assignment with an empty
    // login name.
    const group = person({ id: 'group-guid', displayName: 'sg-finance', kind: 'group', mail: '', userPrincipalName: '' });

    expect(
      matches(assignment({ principalRef: null, loginName: '', principalName: 'Something else' }), group),
    ).toBe(false);
  });
});

describe('findAccess', () => {
  const target = person();

  it('finds the items that name the principal', () => {
    const scans = [
      scan({
        items: [
          { id: 1, path: '/sites/x/Documents/Area 1', name: 'Area 1', isFolder: true, broken: true, assignments: [assignment()] },
          { id: 2, path: '/sites/x/Documents/Area 2', name: 'Area 2', isFolder: true, broken: true, assignments: [assignment({ principalName: 'Someone Else', loginName: 'i:0#.f|membership|other@x.com', principalRef: null })] },
        ],
      }),
    ];

    const result = findAccess(target, scans, new Map());

    expect(result.hits).toHaveLength(1);
    expect(result.hits[0]?.path).toBe('/sites/x/Documents/Area 1');
    expect(result.hits[0]?.library).toBe('Documents');
  });

  it('ignores items that inherit, because they carry no assignments of their own', () => {
    const scans = [
      scan({
        items: [{ id: 1, path: '/sites/x/Documents/a.txt', name: 'a.txt', isFolder: false, broken: false, assignments: [assignment()] }],
      }),
    ];

    expect(findAccess(target, scans, new Map()).hits).toHaveLength(0);
  });

  it('reports a library level grant separately from item level ones', () => {
    // A grant on the library reaches everything inside that inherits, so it is
    // a different fact from a grant on one folder.
    const result = findAccess(target, [scan()], new Map([['list-1', [assignment()]]]));

    expect(result.topLevel).toHaveLength(1);
    expect(result.topLevel[0]?.principalName).toBe('Documents');
    expect(result.hits).toHaveLength(0);
  });

  it('returns nothing for a principal that appears nowhere', () => {
    const scans = [
      scan({ items: [{ id: 1, path: '/x', name: 'x', isFolder: false, broken: true, assignments: [assignment({ principalName: 'Other', loginName: 'other', principalRef: null })] }] }),
    ];

    const result = findAccess(target, scans, new Map());

    expect(result.hits).toHaveLength(0);
    expect(result.topLevel).toHaveLength(0);
  });
});
