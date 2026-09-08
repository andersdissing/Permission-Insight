import { describe, expect, it } from 'vitest';
import { buildCsv, exportFileName, type ExportHeader } from './csv';

const header: ExportHeader = {
  siteTitle: 'Personalesager',
  siteUrl: 'https://contoso.sharepoint.com/sites/hr',
  scope: 'whole site',
  generatedBy: 'jens@contoso.com',
  coverage: 'All sharing links in this site collection.',
};

function lines(csv: string): string[] {
  return csv.split('\r\n');
}

describe('buildCsv', () => {
  it('starts with a byte order mark so Excel reads it as UTF-8', () => {
    // Without this, Danish characters open as mojibake and the file looks
    // corrupt to the person the report was written for.
    const csv = buildCsv(header, ['Path'], [['/sites/hr/Opsigelse Jens Hansen.docx']]);

    expect(csv.charCodeAt(0)).toBe(0xfeff);
  });

  it('carries all five comment lines, coverage included', () => {
    const [first, site, scope, generated, coverage, columns] = lines(
      buildCsv(header, ['Path'], []),
    );

    expect(first).toBe('﻿# Permission Insight export');
    expect(site).toBe('# Site: Personalesager (https://contoso.sharepoint.com/sites/hr)');
    expect(scope).toBe('# Scope: whole site');
    expect(generated).toContain('by jens@contoso.com');
    expect(coverage).toBe('# Coverage: All sharing links in this site collection.');
    expect(columns).toBe('Path');
  });

  it('still states coverage when there are no rows', () => {
    // A report with no rows must not become evidence that there was nothing
    // to find when it may only mean nothing was scanned.
    expect(buildCsv(header, ['Path'], [])).toContain('# Coverage:');
  });

  it('quotes a field containing a comma', () => {
    const csv = buildCsv(header, ['Recipients'], [['jens@contoso.com, anna@contoso.com']]);

    expect(lines(csv).at(-2)).toBe('"jens@contoso.com, anna@contoso.com"');
  });

  it('doubles quotes inside a quoted field', () => {
    const csv = buildCsv(header, ['Path'], [['/sites/hr/He said "no".docx']]);

    expect(lines(csv).at(-2)).toBe('"/sites/hr/He said ""no"".docx"');
  });

  it('quotes a field containing a newline rather than breaking the row', () => {
    const csv = buildCsv(header, ['Note'], [['first\nsecond']]);

    expect(csv).toContain('"first\nsecond"');
  });

  it('writes an empty cell for a missing value rather than the word null', () => {
    const csv = buildCsv(header, ['Expiry', 'HasPassword'], [[null, undefined]]);

    expect(lines(csv).at(-2)).toBe(',');
  });

  it('writes booleans plainly so a spreadsheet can filter on them', () => {
    const csv = buildCsv(header, ['External'], [[true], [false]]);

    expect(lines(csv).slice(-3, -1)).toEqual(['true', 'false']);
  });

  it('keeps Danish characters intact', () => {
    const csv = buildCsv(header, ['Path'], [['/sites/hr/Opsigelse Jørgen Ø. Åberg.docx']]);

    expect(csv).toContain('Opsigelse Jørgen Ø. Åberg.docx');
  });
});

describe('exportFileName', () => {
  it('names the site and the scope and sorts by date', () => {
    const name = exportFileName('Personalesager', 'sharing-links');

    expect(name).toMatch(/^permission-insight-personalesager-sharing-links-\d{4}-\d{2}-\d{2}\.csv$/);
  });

  it('reduces characters a file system would object to', () => {
    expect(exportFileName('Sales & Marketing / EMEA', 'sharing-links')).toContain(
      'sales-marketing-emea',
    );
  });
});
