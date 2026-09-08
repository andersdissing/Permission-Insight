import type { SharingLinkRow } from '../db/sharingLinks';
import { buildCsv, downloadCsv, exportFileName } from './csv';

const COLUMNS = [
  'Path',
  'ItemType',
  'LinkScope',
  'LinkRole',
  'Recipients',
  'External',
  'Expiry',
  'HasPassword',
  'GroupId',
  'Status',
] as const;

interface Options {
  siteTitle: string;
  siteWebUrl: string;
  generatedBy: string;
  rows: SharingLinkRow[];
}

/**
 * Sharing links can always be exported in full: they come from one
 * site-collection call and never depend on which libraries were scanned.
 *
 * Orphaned rows stay in the file. An orphaned group holding an external guest
 * is a finding, and dropping it would make the file quieter than the truth.
 */
export function exportSharingLinks({ siteTitle, siteWebUrl, generatedBy, rows }: Options): number {
  const unresolved = rows.filter((row) => row.detail === null).length;

  const coverage =
    unresolved === 0
      ? 'All sharing links in this site collection, including subsites. Library scans are not included and are not needed: sharing links do not depend on them.'
      : `All sharing links in this site collection, including subsites. ${unresolved} of ${rows.length} had not finished resolving when this file was written and appear with status Unresolved.`;

  const content = buildCsv(
    { siteTitle, siteUrl: siteWebUrl, scope: 'whole site', generatedBy, coverage },
    COLUMNS,
    rows.map(toRow),
  );

  downloadCsv(exportFileName(siteTitle, 'sharing-links'), content);

  return rows.length;
}

function toRow(row: SharingLinkRow) {
  const detail = row.detail;

  if (detail === null) {
    return ['', '', '', '', '', '', '', '', row.groupId, 'Unresolved'];
  }

  const recipients = detail.permission?.recipients ?? detail.members;

  return [
    detail.status === 'Orphaned' ? 'Orphaned' : (detail.path ?? ''),
    detail.itemType,
    detail.permission?.scope ?? '',
    detail.permission?.role ?? '',
    recipients.map((recipient) => recipient.email ?? recipient.name).join('; '),
    recipients.some((recipient) => recipient.external),
    detail.permission?.expiry ?? '',
    detail.permission?.hasPassword ?? '',
    row.groupId,
    detail.status,
  ];
}
