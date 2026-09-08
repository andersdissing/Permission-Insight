import { useEffect, useMemo, useState } from 'react';
import type { ApiClient, Assignment } from '../api/client';
import type { LibraryScan, ScannedItem } from '../db/scans';
import { buildTree, filterToFindings, type TreeNode } from '../tree/buildTree';
import { format } from '../scan/libraryScan';
import { ChevronIcon } from '../components/Icons';
import { DetailPanel } from './DetailPanel';

interface Props {
  api: ApiClient;
  siteId: string;
  siteWebUrl: string;
  scan: LibraryScan;
  onBack: () => void;
  onExport: () => void;
}

type Selection = { kind: 'item'; item: ScannedItem } | { kind: 'library' } | null;

export function LibraryTree({ api, siteId, siteWebUrl, scan, onBack, onExport }: Props) {
  // Off by default. The specification had this on, on the grounds that the
  // filtered view is what the user came for; in practice the full structure
  // with findings highlighted reads better, because it shows what was searched
  // as well as what was found.
  const [onlyFindings, setOnlyFindings] = useState(false);

  // One selection, so the library panel and an item panel cannot both be open.
  const [selected, setSelected] = useState<Selection>(null);

  // The library's own grants, fetched once. Every panel compares against them
  // to separate what was copied down when inheritance broke from what somebody
  // actually added.
  const [parentAccess, setParentAccess] = useState<Assignment[]>([]);

  // The site's grants, which stand in the same relation to the library as the
  // library does to its items. Without them the library panel would mark every
  // row as added here, which is exactly the overstatement the item panels
  // avoid.
  const [siteAccess, setSiteAccess] = useState<Assignment[]>([]);

  useEffect(() => {
    const controller = new AbortController();

    api
      .getListAccess(siteId, scan.listId, controller.signal)
      .then((response) => setParentAccess(response.assignments))
      .catch(() => {
        // Without it every row simply reads as copied, which understates
        // rather than invents. Losing the panel over it would be worse.
      });

    api
      .getSiteAccess(siteId, controller.signal)
      .then((response) => setSiteAccess(response.assignments))
      .catch(() => {
        // Same trade: the library panel degrades to showing no origin marks
        // rather than refusing to open.
      });

    return () => controller.abort();
  }, [api, siteId, scan.listId]);
  const [expanded, setExpanded] = useState<Set<number>>(new Set());

  const full = useMemo(() => buildTree(scan.items), [scan.items]);
  const filtered = useMemo(() => filterToFindings(full), [full]);

  const roots = onlyFindings ? filtered : full;
  const findings = scan.permissionRoots.length;

  const toggle = (id: number) => {
    setExpanded((previous) => {
      const next = new Set(previous);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  return (
    <div className="tree-screen">
      <div className="tab-header">
        <nav className="breadcrumb">
          <button type="button" className="icon-button" aria-label="Back" onClick={onBack}>
            <ChevronIcon className="chevron-back" />
          </button>
          <button type="button" className="crumb-link" onClick={onBack}>
            Lists and libraries
          </button>
          <span className="crumb-separator">/</span>
          <span className="crumb-current">{scan.listTitle}</span>
        </nav>

        <div className="tab-actions">
          <label className="system-toggle">
            <input
              type="checkbox"
              checked={onlyFindings}
              onChange={(event) => setOnlyFindings(event.target.checked)}
            />
            Only unique permissions
          </label>
          <button type="button" className="text-button" onClick={onExport}>
            Export
          </button>
        </div>
      </div>

      {/* Kept visible in both modes, so zero findings is distinguishable from
          a library nobody has scanned. */}
      <p className="result-count">
        {onlyFindings
          ? `${format(findings)} items with unique permissions`
          : `${format(scan.items.length)} items · ${format(findings)} with unique permissions`}
      </p>

      <div className="tree-area">
        <ul className="tree">
          {/* The library itself, above its contents, and in both modes. Where
              it holds unique permissions it is the root the whole tree
              inherits from — the widest grant on this screen, and the one
              thing the screen never showed. */}
          <LibraryRootRow
            title={scan.listTitle}
            isPermissionRoot={scan.libraryIsPermissionRoot}
            count={parentAccess.length}
            selected={selected?.kind === 'library'}
            onSelect={() => setSelected({ kind: 'library' })}
          />

          {roots.map((node) => (
            <TreeRow
              key={node.item.id}
              node={node}
              onlyFindings={onlyFindings}
              expanded={expanded}
              selectedId={selected?.kind === 'item' ? selected.item.id : null}
              onToggle={toggle}
              onSelect={(item) => setSelected({ kind: 'item', item })}
            />
          ))}
        </ul>

        {onlyFindings && findings === 0 && (
          <EverythingInherits
            libraryName={scan.listTitle}
            isPermissionRoot={scan.libraryIsPermissionRoot}
            onShowAll={() => setOnlyFindings(false)}
          />
        )}

        {selected !== null && (
            /* Keyed by what is open, so choosing another row remounts the
               panel and its scroll position starts at the top rather than
               wherever the previous item had been read to. */
            <DetailPanel
              key={selected.kind === 'library' ? 'library' : selected.item.id}
              api={api}
              siteWebUrl={siteWebUrl}
              listId={scan.listId}
              scope={selected.kind === 'library' ? 'library' : 'item'}
              item={
                selected.kind === 'library'
                  ? {
                      // Not a real list item, and given an id no item can have.
                      // The library's assignments are what getListAccess
                      // already returned for the diff, so the panel needs no
                      // further call.
                      id: -1,
                      name: scan.listTitle,
                      path: null,
                      isFolder: true,
                      broken: true,
                      assignments: parentAccess,
                    }
                  : selected.item
              }
            parentAccess={selected.kind === 'library' ? siteAccess : parentAccess}
            onClose={() => setSelected(null)}
          />
        )}
      </div>
    </div>
  );
}

/**
 * The library itself, as the first row of its own tree.
 *
 * A library with unique permissions is not a finding on its own — CLAUDE.md is
 * explicit that breaking inheritance copies the existing assignments, so the
 * library starts out identical to the site. It is the point where two
 * permission sets begin to drift, and everything below inherits from here
 * rather than from the site. That makes it the widest scope on this screen,
 * and it was the one scope the screen did not show.
 *
 * Rendered whether or not the library is a root, because "inherits from the
 * site" is an answer too, and an absent row would leave the reader to assume
 * one.
 */
function LibraryRootRow({
  title,
  isPermissionRoot,
  count,
  selected,
  onSelect,
}: {
  title: string;
  isPermissionRoot: boolean;
  count: number;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <li className={`tree-row library-root${selected ? ' selected' : ''}`}>
      <span className="expander-spacer" />
      <span className="type-icon" aria-hidden="true">
        ▤
      </span>

      <button type="button" className="tree-name" onClick={onSelect}>
        {title}
      </button>

      <span className="badges">
        {isPermissionRoot ? (
          <span className="badge warning">Unique permissions</span>
        ) : (
          <span className="badge">Inherits from the site</span>
        )}
        {count > 0 && <span className="badge">{count === 1 ? '1 grant' : `${count} grants`}</span>}
      </span>
    </li>
  );
}

function EverythingInherits({
  libraryName,
  isPermissionRoot,
  onShowAll,
}: {
  libraryName: string;
  isPermissionRoot: boolean;
  onShowAll: () => void;
}) {
  // Zero findings is a good answer, not a missing one, so this must not read
  // as an error.
  //
  // But it is only "access comes entirely from the site" when the library
  // inherits. Where the library is itself a permission root, saying that would
  // be wrong in the most misleading direction available: everything below
  // inherits from a set of permissions that is no longer the site's.
  return (
    <div className="inherits">
      <span className="inherits-check" aria-hidden="true">
        ✓
      </span>
      <h3>Every item inherits</h3>
      <p>
        {isPermissionRoot ? (
          <>
            No item in {libraryName} has unique permissions or sharing links. They all inherit from
            the library, which has its own permissions — open it above to see who that grants.
          </>
        ) : (
          <>
            Nothing in {libraryName} has unique permissions or sharing links. Access comes entirely
            from the site.
          </>
        )}
      </p>
      <button type="button" className="text-button" onClick={onShowAll}>
        Show full structure
      </button>
    </div>
  );
}

function TreeRow({
  node,
  onlyFindings,
  expanded,
  selectedId,
  onToggle,
  onSelect,
}: {
  node: TreeNode;
  onlyFindings: boolean;
  expanded: Set<number>;
  selectedId: number | null;
  onToggle: (id: number) => void;
  onSelect: (item: ScannedItem) => void;
}) {
  // Filtered mode is already pruned to what the user came for, so a collapsed
  // scaffold would hide exactly that. Unfiltered mode has something to hide,
  // so it starts closed.
  const open = onlyFindings ? true : expanded.has(node.item.id);
  const hasChildren = node.children.length > 0;
  const clickable = node.hasFinding;
  const isSelected = selectedId === node.item.id;

  return (
    <>
      <li
        className={`tree-row${clickable ? ' clickable' : ' muted'}${isSelected ? ' selected' : ''}`}
        style={{ paddingLeft: `${node.depth * 24 + 8}px` }}
      >
        {hasChildren && !onlyFindings ? (
          <button
            type="button"
            className="expander"
            aria-expanded={open}
            onClick={() => onToggle(node.item.id)}
          >
            {open ? '−' : '+'}
          </button>
        ) : (
          <span className="expander-spacer" />
        )}

        <span className="type-icon" aria-hidden="true">
          {node.item.isFolder ? '▸' : '·'}
        </span>

        {clickable ? (
          <button type="button" className="tree-name" onClick={() => onSelect(node.item)}>
            {node.item.name}
          </button>
        ) : (
          <span className="tree-name">{node.item.name}</span>
        )}

        {/* Kept while the panel is open. The specification suppressed them as
            a duplicate, but a row that loses its badges on click reads as
            though selecting it changed something. */}
        {node.hasFinding && <Badges assignments={node.item.assignments ?? []} />}
      </li>

      {open &&
        (hasChildren ? (
          node.children.map((child) => (
            <TreeRow
              key={child.item.id}
              node={child}
              onlyFindings={onlyFindings}
              expanded={expanded}
              selectedId={selectedId}
              onToggle={onToggle}
              onSelect={onSelect}
            />
          ))
        ) : node.item.isFolder && !onlyFindings ? (
          <li className="tree-row empty-folder" style={{ paddingLeft: `${(node.depth + 1) * 24 + 8}px` }}>
            Empty - no files
          </li>
        ) : null)}
    </>
  );
}

function Badges({ assignments }: { assignments: Assignment[] }) {
  const links = assignments.filter((assignment) => assignment.source === 'Sharing link');
  const direct = assignments.filter((assignment) => assignment.source === 'Direct grant');

  // Every sharing link breaks inheritance, so links are already a subset of
  // what the tree shows. They earn their own badges because the scope is the
  // thing that decides how exposed the item is.
  const scopes = [...new Set(links.map((link) => link.linkScope).filter(Boolean))] as string[];

  return (
    <span className="badges">
      {/* The category that matters most: a direct grant never expires and is
          rarely documented. */}
      {direct.length > 0 && <span className="badge">Direct grant</span>}

      {scopes.map((scope) => (
        <span key={scope} className={scope === 'anonymous' ? 'badge warning' : 'badge'}>
          {scopeLabel(scope)}
        </span>
      ))}

      {links.length > 1 && <span className="badge">{links.length} links</span>}
      {links.length > 0 && scopes.length === 0 && <span className="badge">Sharing link</span>}
    </span>
  );
}

function scopeLabel(scope: string): string {
  switch (scope) {
    case 'anonymous':
      return 'Anyone';
    case 'organization':
      return 'Organization';
    case 'users':
      return 'Specific people';
    case 'existingAccess':
      return 'Existing access';
    default:
      return scope;
  }
}
