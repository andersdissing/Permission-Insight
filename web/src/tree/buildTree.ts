import type { ScannedItem } from '../db/scans';

export interface TreeNode {
  item: ScannedItem;
  children: TreeNode[];
  /** This item has unique permissions of its own. */
  hasFinding: boolean;
  /**
   * Present only to keep a path intact in filtered mode. Muted, unbadged and
   * not clickable: without that distinction a user reads every folder in the
   * tree as having unique permissions, which is the opposite of what the
   * filtered view means.
   */
  scaffoldOnly: boolean;
  depth: number;
}

/**
 * Builds the folder hierarchy from the flat item list.
 *
 * The REST endpoint returns every item at every depth with folders appearing
 * as items, so the shape comes from the paths rather than from recursion.
 * Parentage is derived by trimming the last path segment; anything whose
 * parent is missing hangs off the root, so a gap in the data loses a level of
 * indentation rather than losing the item.
 */
export function buildTree(items: ScannedItem[]): TreeNode[] {
  const nodes = new Map<string, TreeNode>();
  const roots: TreeNode[] = [];

  const sorted = [...items].sort(comparePaths);

  for (const item of sorted) {
    if (item.path === null) {
      continue;
    }

    const node: TreeNode = {
      item,
      children: [],
      hasFinding: item.broken,
      scaffoldOnly: false,
      depth: 0,
    };

    nodes.set(normalise(item.path), node);
  }

  for (const node of nodes.values()) {
    const parentPath = parentOf(normalise(node.item.path ?? ''));
    const parent = parentPath === null ? undefined : nodes.get(parentPath);

    if (parent) {
      parent.children.push(node);
    } else {
      roots.push(node);
    }
  }

  assignDepth(roots, 0);
  return roots;
}

/**
 * Keeps items with findings and every ancestor folder of one, pruning any
 * branch that contains nothing.
 *
 * A folder kept only because something beneath it was found is marked
 * scaffoldOnly. A folder that has a finding of its own *and* contains more
 * below keeps both its badge and its expander.
 *
 * Because a folder survives only when it contains a finding, filtered mode can
 * never show an empty expanded folder — which is why "Empty - no files"
 * belongs to the unfiltered view alone.
 */
export function filterToFindings(roots: TreeNode[]): TreeNode[] {
  const keep = (node: TreeNode): TreeNode | null => {
    const children = node.children
      .map(keep)
      .filter((child): child is TreeNode => child !== null);

    if (!node.hasFinding && children.length === 0) {
      return null;
    }

    return {
      ...node,
      children,
      scaffoldOnly: !node.hasFinding,
    };
  };

  return roots.map(keep).filter((node): node is TreeNode => node !== null);
}

export function countFindings(items: ScannedItem[]): number {
  return items.filter((item) => item.broken).length;
}

/** Server-relative paths are case-insensitive in SharePoint and may carry a trailing slash. */
function normalise(path: string): string {
  return path.replace(/\/+$/, '').toLowerCase();
}

function parentOf(path: string): string | null {
  const cut = path.lastIndexOf('/');
  return cut <= 0 ? null : path.slice(0, cut);
}

/**
 * Folders before files at each level, then by name. Sorting by path first
 * means a parent is always seen before its children.
 */
function comparePaths(left: ScannedItem, right: ScannedItem): number {
  const leftPath = left.path ?? '';
  const rightPath = right.path ?? '';

  const depthDifference = segments(leftPath) - segments(rightPath);
  if (depthDifference !== 0) {
    return depthDifference;
  }

  if (left.isFolder !== right.isFolder) {
    return left.isFolder ? -1 : 1;
  }

  return leftPath.localeCompare(rightPath, undefined, { numeric: true, sensitivity: 'base' });
}

function segments(path: string): number {
  return path.split('/').filter(Boolean).length;
}

function assignDepth(nodes: TreeNode[], depth: number): void {
  for (const node of nodes) {
    node.depth = depth;
    node.children.sort(compareNodes);
    assignDepth(node.children, depth + 1);
  }
}

function compareNodes(left: TreeNode, right: TreeNode): number {
  return comparePaths(left.item, right.item);
}
