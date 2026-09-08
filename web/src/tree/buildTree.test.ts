import { describe, expect, it } from 'vitest';
import type { ScannedItem } from '../db/scans';
import { buildTree, filterToFindings, type TreeNode } from './buildTree';

let nextId = 1;

function folder(path: string, broken = false): ScannedItem {
  return { id: nextId++, path, name: path.split('/').pop() ?? '', isFolder: true, broken };
}

function file(path: string, broken = false): ScannedItem {
  return { id: nextId++, path, name: path.split('/').pop() ?? '', isFolder: false, broken };
}

/** Flattens to "depth:name" so shape and indentation are both asserted. */
function flatten(nodes: TreeNode[], into: string[] = []): string[] {
  for (const node of nodes) {
    into.push(`${node.depth}:${node.item.name}${node.scaffoldOnly ? ' (scaffold)' : ''}`);
    flatten(node.children, into);
  }
  return into;
}

describe('buildTree', () => {
  it('nests items under their folders from the flat list', () => {
    const tree = buildTree([
      file('/sites/f/Documents/Area 1/doc.txt'),
      folder('/sites/f/Documents/Area 1'),
      folder('/sites/f/Documents'),
    ]);

    expect(flatten(tree)).toEqual(['0:Documents', '1:Area 1', '2:doc.txt']);
  });

  it('handles five levels without recursing over the API', () => {
    const tree = buildTree([
      folder('/sites/f/Documents'),
      folder('/sites/f/Documents/Deep'),
      folder('/sites/f/Documents/Deep/One'),
      folder('/sites/f/Documents/Deep/One/Two'),
      folder('/sites/f/Documents/Deep/One/Two/Three'),
      file('/sites/f/Documents/Deep/One/Two/Three/deep.txt', true),
    ]);

    expect(flatten(tree)).toEqual([
      '0:Documents',
      '1:Deep',
      '2:One',
      '3:Two',
      '4:Three',
      '5:deep.txt',
    ]);
  });

  it('puts folders before files at the same level', () => {
    const tree = buildTree([
      folder('/sites/f/Documents'),
      file('/sites/f/Documents/a.txt'),
      folder('/sites/f/Documents/Zed'),
    ]);

    expect(flatten(tree)).toEqual(['0:Documents', '1:Zed', '1:a.txt']);
  });

  it('keeps an item whose parent folder is missing rather than dropping it', () => {
    // A gap in the data should cost a level of indentation, not the finding.
    const tree = buildTree([file('/sites/f/Documents/Orphan/file.txt', true)]);

    expect(flatten(tree)).toEqual(['0:file.txt']);
  });

  it('matches paths case-insensitively, as SharePoint does', () => {
    const tree = buildTree([
      folder('/sites/f/Documents'),
      file('/sites/f/DOCUMENTS/doc.txt'),
    ]);

    expect(flatten(tree)).toEqual(['0:Documents', '1:doc.txt']);
  });
});

describe('filterToFindings', () => {
  it('keeps a deep finding and every ancestor, marking the ancestors as scaffold', () => {
    const tree = buildTree([
      folder('/sites/f/Documents'),
      folder('/sites/f/Documents/Deep'),
      folder('/sites/f/Documents/Deep/One'),
      file('/sites/f/Documents/Deep/One/found.txt', true),
    ]);

    expect(flatten(filterToFindings(tree))).toEqual([
      '0:Documents (scaffold)',
      '1:Deep (scaffold)',
      '2:One (scaffold)',
      '3:found.txt',
    ]);
  });

  it('prunes a branch that contains nothing', () => {
    const tree = buildTree([
      folder('/sites/f/Documents'),
      folder('/sites/f/Documents/Bulk'),
      file('/sites/f/Documents/Bulk/a.txt'),
      file('/sites/f/Documents/Bulk/b.txt'),
      folder('/sites/f/Documents/Area 1', true),
    ]);

    expect(flatten(filterToFindings(tree))).toEqual(['0:Documents (scaffold)', '1:Area 1']);
  });

  it('a folder with its own finding and findings below is not scaffold', () => {
    // It keeps its badge and its expander both.
    const tree = buildTree([
      folder('/sites/f/Documents'),
      folder('/sites/f/Documents/Area 1', true),
      file('/sites/f/Documents/Area 1/shared.txt', true),
    ]);

    expect(flatten(filterToFindings(tree))).toEqual([
      '0:Documents (scaffold)',
      '1:Area 1',
      '2:shared.txt',
    ]);
  });

  it('never leaves an empty folder, so "Empty - no files" cannot occur here', () => {
    const tree = buildTree([
      folder('/sites/f/Documents'),
      folder('/sites/f/Documents/Empty folder'),
    ]);

    const filtered = filterToFindings(tree);

    expect(filtered).toEqual([]);
  });

  it('returns nothing at all for a library with no findings', () => {
    // Which is what drives the "Everything inherits" empty state, and is a
    // good answer rather than a missing one.
    const tree = buildTree([
      folder('/sites/f/CleanLib'),
      file('/sites/f/CleanLib/a.txt'),
    ]);

    expect(filterToFindings(tree)).toEqual([]);
  });

  it('leaves the unfiltered tree untouched', () => {
    const items = [folder('/sites/f/Documents'), file('/sites/f/Documents/a.txt')];
    const tree = buildTree(items);

    filterToFindings(tree);

    expect(flatten(tree)).toEqual(['0:Documents', '1:a.txt']);
  });
});
