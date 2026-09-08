/**
 * CSV, UTF-8 with a byte order mark so Excel opens Danish characters
 * correctly.
 */
const BYTE_ORDER_MARK = '\uFEFF';

export interface ExportHeader {
  siteTitle: string;
  siteUrl: string;
  /** "whole site", a library name, or "person: {name}". */
  scope: string;
  generatedBy: string;
  /** What the file covers and, more importantly, what it does not. */
  coverage: string;
}

/**
 * Every file opens with these comment lines.
 *
 * The coverage line is not optional. A file leaves the application's access
 * control the moment it is written, and will be read by someone who never saw
 * the screen. Without it, a report with no rows becomes evidence that there
 * was nothing to find, when it may only mean the scan had not finished.
 */
export function buildCsv(
  header: ExportHeader,
  columns: readonly string[],
  rows: readonly (readonly (string | number | boolean | null | undefined)[])[],
): string {
  const lines = [
    '# Permission Insight export',
    `# Site: ${header.siteTitle} (${header.siteUrl})`,
    `# Scope: ${header.scope}`,
    `# Generated: ${new Date().toISOString()} by ${header.generatedBy}`,
    `# Coverage: ${header.coverage}`,
    columns.map(escape).join(','),
    ...rows.map((row) => row.map(escape).join(',')),
  ];

  // The byte order mark, written as an escape rather than a literal so it
  // survives an editor that strips invisible characters. Without it Excel
  // reads the file as the local code page and Danish characters come out
  // wrong, which is acceptance test 1 in docs/09-export.md.
  return `${BYTE_ORDER_MARK}${lines.join('\r\n')}\r\n`;
}

function escape(value: string | number | boolean | null | undefined): string {
  if (value === null || value === undefined) {
    return '';
  }

  const text = String(value);

  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

export function downloadCsv(fileName: string, content: string): void {
  const blob = new Blob([content], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);

  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();

  URL.revokeObjectURL(url);
}

/** A file name that sorts by date and says what it holds. */
export function exportFileName(siteTitle: string, scope: string): string {
  const safe = (text: string) => text.replace(/[^\w-]+/g, '-').replace(/^-+|-+$/g, '').toLowerCase();
  const date = new Date().toISOString().slice(0, 10);

  return `permission-insight-${safe(siteTitle)}-${safe(scope)}-${date}.csv`;
}
