import type { LibraryScan } from '../db/scans';
import { buildCsv, downloadCsv, exportFileName } from './csv';

const COLUMNS = [
  'Path',
  'ItemType',
  'List',
  'Source',
  'Principal',
  'PrincipalType',
  'Role',
  'LinkScope',
  'Expiry',
] as const;

interface Options {
  siteTitle: string;
  siteWebUrl: string;
  generatedBy: string;
  scans: LibraryScan[];
  /** Libraries the user has not scanned. Named in the coverage line, not hidden. */
  unscanned: string[];
  /**
   * Libraries that hold nothing. Named separately because they are not a gap
   * in the report — there was nothing in them to find — but a reader comparing
   * this file against the site would otherwise see them missing and wonder
   * which of the two it was.
   */
  empty?: string[];
}

/**
 * Broken inheritance, exported per library or for the whole site.
 *
 * One row per principal rather than per item, so the file can be filtered by
 * person in a spreadsheet. Limited Access never appears, matching the screen.
 */
export function exportBrokenInheritance({
  siteTitle,
  siteWebUrl,
  generatedBy,
  scans,
  unscanned,
  empty = [],
}: Options): number {
  const rows = scans.flatMap(toRows);

  const scope = scans.length === 1 && scans[0] ? scans[0].listTitle : 'whole site';

  const covered = scans.map((scan) => scan.listTitle).join(', ') || 'nothing';

  // Two different absences, said differently. A library nobody scanned is a
  // gap in this report; a library holding nothing is not.
  const gaps =
    unscanned.length === 0
      ? 'Every list and library in this site that holds anything has been scanned.'
      : `NOT included, because they have not been scanned: ${unscanned.join(', ')}.`;

  const emptied =
    empty.length === 0 ? '' : ` Empty, so nothing to scan: ${empty.join(', ')}.`;

  const coverage = `Covers ${covered}. ${gaps}${emptied}`;

  const content = buildCsv(
    { siteTitle, siteUrl: siteWebUrl, scope, generatedBy, coverage },
    COLUMNS,
    rows,
  );

  downloadCsv(exportFileName(siteTitle, scope), content);

  return rows.length;
}

function toRows(scan: LibraryScan) {
  return scan.items
    .filter((item) => item.broken)
    .flatMap((item) => {
      const assignments = item.assignments ?? [];

      if (assignments.length === 0) {
        // Broken but unreadable. Saying so beats omitting the row, which would
        // read as inheriting.
        return [
          [
            item.path ?? '',
            item.isFolder ? 'Folder' : 'File',
            scan.listTitle,
            'Unknown',
            '',
            '',
            '',
            '',
            '',
          ],
        ];
      }

      return assignments.map((assignment) => [
        item.path ?? '',
        item.isFolder ? 'Folder' : 'File',
        scan.listTitle,
        assignment.source,
        assignment.principalName,
        principalType(assignment.principalType),
        assignment.roles.join('; '),
        // Scope and expiry live on the permission object, which the sharing
        // links tab reads. This export deliberately does not guess at them.
        '',
        '',
      ]);
    });
}

function principalType(value: number): string {
  switch (value) {
    case 1:
      return 'User';
    case 2:
      return 'Distribution list';
    case 4:
      return 'Security group';
    case 8:
      return 'SharePoint group';
    default:
      return 'Unknown';
  }
}
