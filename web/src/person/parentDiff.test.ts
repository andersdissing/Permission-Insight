import { describe, expect, it } from 'vitest';
import type { Assignment } from '../api/client';
import { allCopied, originOf, originOfLink } from './parentDiff';

function assignment(overrides: Partial<Assignment> = {}): Assignment {
  return {
    principalId: 0,
    principalName: 'Medlemmer af PI Test Data',
    principalType: 8,
    loginName: 'Medlemmer af PI Test Data',
    roles: ['write'],
    source: 'Direct grant',
    ...overrides,
  };
}

describe('originOf', () => {
  it('calls an assignment copied when the parent has the same principal and role', () => {
    // SharePoint copies the parent's assignments down when inheritance is
    // broken, so a freshly broken folder arrives carrying Owners, Members and
    // Visitors. None of that is what anyone broke inheritance for.
    expect(originOf(assignment(), [assignment()])).toBe('copied');
  });

  it('calls an assignment added when the parent does not have that principal', () => {
    const parent = [assignment({ principalName: 'Owners', loginName: 'Owners' })];
    const added = assignment({ principalName: 'PI Test Alpha', loginName: 'i:0#.f|membership|alpha@x.com' });

    expect(originOf(added, parent)).toBe('added');
  });

  it('calls it changed when the principal is present above but with a different role', () => {
    const parent = [assignment({ roles: ['read'] })];

    expect(originOf(assignment({ roles: ['write'] }), parent)).toBe('changed');
  });

  it('prefers the principal id over the name when both are available', () => {
    const parent = [assignment({ principalRef: 'guid-1', principalName: 'Renamed since' })];
    const child = assignment({ principalRef: 'GUID-1', principalName: 'Current name' });

    expect(originOf(child, parent)).toBe('copied');
  });

  it('ignores role ordering and casing', () => {
    const parent = [assignment({ roles: ['Read', 'Write'] })];

    expect(originOf(assignment({ roles: ['write', 'read'] }), parent)).toBe('copied');
  });

  it('treats everything as added when the parent could not be read', () => {
    // An empty parent understates rather than invents: every row highlights,
    // which is noisy but never hides a real finding.
    expect(originOf(assignment(), [])).toBe('added');
  });
});

describe('originOfLink', () => {
  function sharingLink(overrides: Partial<Assignment> = {}): Assignment {
    return assignment({
      source: 'Sharing link',
      principalName: 'Anyone in the organization',
      principalType: 0,
      loginName: '',
      principalRef: null,
      linkScope: 'organization',
      roles: ['read'],
      ...overrides,
    });
  }

  it('treats a link with no identifier as created here', () => {
    // Every organisation link shares a display name, so name matching would
    // decide a link made on this item is a copy of an unrelated one on the
    // library — and hide the very thing that broke inheritance.
    const parent = [sharingLink()];

    expect(originOfLink(sharingLink(), parent)).toBe('added');
  });

  it('does not fall back to the display name', () => {
    const parent = [sharingLink({ principalRef: 'link-1' })];

    expect(originOfLink(sharingLink({ principalRef: 'link-2' }), parent)).toBe('added');
  });

  it('recognises the same link on the parent by its identifier', () => {
    const parent = [sharingLink({ principalRef: 'link-1' })];

    expect(originOfLink(sharingLink({ principalRef: 'LINK-1' }), parent)).toBe('copied');
  });

  it('reports a role change on a link that is otherwise the same', () => {
    const parent = [sharingLink({ principalRef: 'link-1', roles: ['read'] })];

    expect(originOfLink(sharingLink({ principalRef: 'link-1', roles: ['write'] }), parent)).toBe(
      'changed',
    );
  });
});

describe('allCopied', () => {
  it('is true when a broken item carries nothing its parent did not', () => {
    const parent = [assignment(), assignment({ principalName: 'Owners', loginName: 'Owners' })];

    expect(allCopied([assignment()], parent)).toBe(true);
  });

  it('is false as soon as one entry was added', () => {
    const parent = [assignment()];
    const item = [assignment(), assignment({ principalName: 'Alpha', loginName: 'alpha' })];

    expect(allCopied(item, parent)).toBe(false);
  });

  it('is false for an item with no assignments at all', () => {
    expect(allCopied([], [assignment()])).toBe(false);
  });
});
