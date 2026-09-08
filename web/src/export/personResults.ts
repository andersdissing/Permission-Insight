import type { PrincipalAccess } from '../person/findAccess';
import { buildCsv, downloadCsv, exportFileName } from './csv';

import { kindLabel } from '../person/localPrincipals';

const COLUMNS = ['Principal', 'PrincipalKind', 'Scope', 'Path', 'Role', 'Source'] as const;

interface Options {
  siteTitle: string;
  siteWebUrl: string;
  generatedBy: string;
  results: PrincipalAccess[];
  scanned: string[];
  unscanned: string[];
}

/**
 * Exports what the search found, with the coverage caveat carried into the
 * file.
 *
 * A file with no rows must still say what was covered. Without that, an empty
 * report becomes evidence that somebody had no access, when it may only mean
 * two libraries were never scanned — or that their access arrives through a
 * group nobody searched for.
 */
export function exportPersonResults({
  siteTitle,
  siteWebUrl,
  generatedBy,
  results,
  scanned,
  unscanned,
}: Options): number {
  const names = results.map((result) => result.principal.displayName).join(', ');

  const coverage =
    (unscanned.length === 0
      ? `Covers ${scanned.join(', ') || 'nothing'}. Every list and library in this site has been scanned. `
      : `Covers ${scanned.join(', ') || 'nothing'}. NOT included, because they have not been scanned: ${unscanned.join(', ')}. `) +
    'Direct assignments only: access reached through a group appears against the group, not the person.';

  const rows = results.flatMap((result) => [
    ...result.topLevel.map((assignment) => [
      result.principal.displayName,
      kindLabel(result.principal.kind),
      'Top level',
      assignment.principalName,
      assignment.roles.join('; '),
      assignment.source,
    ]),
    ...result.hits.map((hit) => [
      result.principal.displayName,
      kindLabel(result.principal.kind),
      hit.library,
      hit.path,
      hit.assignment.roles.join('; '),
      hit.assignment.source,
    ]),
  ]);

  const content = buildCsv(
    { siteTitle, siteUrl: siteWebUrl, scope: `principals: ${names}`, generatedBy, coverage },
    COLUMNS,
    rows,
  );

  downloadCsv(exportFileName(siteTitle, 'principals'), content);

  return rows.length;
}
