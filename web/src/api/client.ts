import type { AppConfig } from '../config';
import type { Session } from '../auth/msal';

export interface Me {
  objectId: string;
  displayName: string;
  mail: string | null;
}

export interface Site {
  id: string;
  title: string;
  /** Server-relative, which is what the picker shows. */
  url: string;
  webUrl: string;
}

export interface SiteSearchResponse {
  results: Site[];
  /** More sites matched than were returned, so the query needs refining. */
  truncated: boolean;
}

/** One hidden site group created by a sharing link, before it is resolved. */
export interface SharingLinkGroup {
  groupId: number;
  itemGuid: string;
  /** Almost always "Flexible" in modern SharePoint, so it proves a link exists and nothing more. */
  kind: string;
  linkGuid: string;
}

export interface SharingLinksResponse {
  siteTitle: string;
  siteWebUrl: string;
  links: SharingLinkGroup[];
}

export interface Recipient {
  name: string;
  email: string | null;
  external: boolean;
}

export interface LinkPermission {
  linkId: string;
  /** anonymous, organization, users or existingAccess. */
  scope: string;
  role: string;
  recipients: Recipient[];
  expiry: string | null;
  hasPassword: boolean;
}

export type SharingLinkStatus =
  | 'Resolved'
  | 'Orphaned'
  | 'ScopeUnavailable'
  /** SharePoint refused to say whether the item exists. Not orphaned — that would assert a deletion. */
  | 'ItemUnreadable';

export interface SharingLinkDetail {
  itemGuid: string;
  linkGuid: string;
  status: SharingLinkStatus;
  path: string | null;
  itemType: string;
  permission: LinkPermission | null;
  members: Recipient[];
  /** The group's membership couldn't be read. The rest of the row still stands. */
  membersUnavailable?: boolean;
  /**
   * Why the audience couldn't be determined: "outside-library" or
   * "link-not-matched". Set only when status is ScopeUnavailable.
   */
  scopeReason?: string | null;
}

export interface ListSummary {
  id: string;
  title: string;
  /**
   * The size badge and the progress denominator.
   *
   * Zero means the list is empty, and an empty list is not scanned at all.
   * Null means SharePoint did not report a count, which is not the same thing
   * and must not skip the list.
   */
  itemCount: number | null;
  isDocumentLibrary: boolean;
  isDefaultDocumentLibrary: boolean;
  /**
   * True of the list object itself, which says nothing about its contents.
   * Null when SharePoint did not report it — a different answer from false.
   */
  hasUniqueRoleAssignments: boolean | null;
  isSystem: boolean;
}

export interface ListsResponse {
  siteTitle: string;
  siteWebUrl: string;
  lists: ListSummary[];
}

export interface ListItem {
  id: number;
  broken: boolean;
  path: string | null;
  name: string | null;
  isFolder: boolean;
}

export interface ListItemsResponse {
  items: ListItem[];
  /** Opaque. Hand it straight back to fetch the next page. */
  nextToken: string | null;
  /**
   * False when SharePoint returned items without HasUniqueRoleAssignments,
   * which means the identity can read content but not permissions. Every item
   * then looks as though it inherits, so a scan must refuse rather than report
   * a clean library.
   */
  permissionDataAvailable: boolean;
}

/**
 * A grant that reaches the whole organisation in one row.
 *
 * The backend detects these from the claim rather than the display name, which
 * is localised — see TenantWideClaim. `label` is the tool's own English name,
 * so a report reads the same whatever language the tenant runs in; the
 * assignment's `principalName` still carries whatever the tenant calls it.
 */
export interface TenantWideGrant {
  /** Stable and language independent: "everyone", "everyone-except-external", "all-windows-users". */
  code: string;
  label: string;
  description: string;
  /** True when guests are inside the grant, which changes how urgent it is. */
  includesExternal: boolean;
}

export interface Assignment {
  principalId: number;
  principalName: string;
  /** 1 user, 2 distribution list, 4 security group, 8 SharePoint group. */
  principalType: number;
  loginName: string;
  /** The principal's own id: a SharePoint group number, or a directory object id. Drives deep links and group expansion. */
  principalRef?: string | null;
  roles: string[];
  /** "Sharing link" or "Direct grant". */
  source: string;
  /** anonymous, organization, users or existingAccess. Present on sharing links only. */
  linkScope?: string | null;
  expiry?: string | null;
  /** True when the grant comes from the site rather than this object. */
  inherited?: boolean;
  /** Non-null when this single row reaches everybody. The highest-value finding the tool reports. */
  tenantWide?: TenantWideGrant | null;
}

export interface ItemPermissionsResponse {
  itemId: number;
  assignments: Assignment[];
}

/**
 * Access at the site itself, which reaches every library and item that still
 * inherits.
 *
 * `determined` is false when the site's own permissions could not be derived —
 * every list has unique permissions, or SharePoint did not report inheritance.
 * That is not the same as "nobody has access", and the screen must not render
 * it as an empty list.
 */
export interface SiteAccessResponse {
  siteTitle: string;
  siteWebUrl: string;
  determined: boolean;
  reason?: string;
  /** The inheriting list the answer was read from, named so it can be checked. */
  derivedFrom?: { listId: string; listTitle: string } | null;
  assignments: Assignment[];
}

export interface GroupMember {
  name: string;
  email: string | null;
  external: boolean;
  principalType: number;
  /** The chain stops here. The row must say so rather than render as empty. */
  isEntraGroup: boolean;
}

export interface GroupMembersResponse {
  groupId: number;
  members: GroupMember[];
}

export interface Person {
  id: string;
  displayName: string;
  mail: string;
  userPrincipalName: string;
  /** "user" or "group". */
  kind: string;
}

export interface PeopleResponse {
  results: Person[];
}

export interface EffectiveLevel {
  level: string;
  high: number;
  low: number;
}

export interface EffectivePermissionsResponse {
  site: EffectiveLevel | null;
  list: EffectiveLevel | null;
  items: { itemId: number; level: string; high: number; low: number }[];
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    /** From Retry-After. SharePoint says how long to wait; believe it. */
    readonly retryAfterSeconds: number | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** Present on a site_not_onboarded error: what an administrator has to run. */
  grantCommand: string | null = null;

  /** The caller is signed in but not assigned the app role. */
  get isMissingRole(): boolean {
    return this.status === 403;
  }

  get isThrottled(): boolean {
    return this.status === 429;
  }

  /**
   * The site exists and can be read, but the tool has not been granted the
   * elevated access that reading permissions needs. A missing step, not a
   * fault, and the only failure with a specific remedy.
   */
  get isSiteNotOnboarded(): boolean {
    return this.code === 'site_not_onboarded';
  }
}

export class ApiClient {
  constructor(
    private readonly config: AppConfig,
    private readonly session: Session,
  ) {}

  me(): Promise<Me> {
    return this.get<Me>('/me');
  }

  searchSites(query: string, signal?: AbortSignal): Promise<SiteSearchResponse> {
    return this.get<SiteSearchResponse>(`/sites?q=${encodeURIComponent(query)}`, signal);
  }

  /** Stage one: every sharing link in the site collection, from one call. */
  getSharingLinks(siteId: string, signal?: AbortSignal): Promise<SharingLinksResponse> {
    return this.get<SharingLinksResponse>(`/sharing-links?siteId=${encodeURIComponent(siteId)}`, signal);
  }

  /** Stage two: one link, resolved into a path with scope, recipients and expiry. */
  getSharingLinkDetail(
    siteId: string,
    link: SharingLinkGroup,
    signal?: AbortSignal,
  ): Promise<SharingLinkDetail> {
    const query = new URLSearchParams({
      siteId,
      groupId: String(link.groupId),
      itemGuid: link.itemGuid,
      linkGuid: link.linkGuid,
    });

    return this.get<SharingLinkDetail>(`/sharing-links/detail?${query.toString()}`, signal);
  }

  getLists(siteId: string, signal?: AbortSignal): Promise<ListsResponse> {
    return this.get<ListsResponse>(`/lists?siteId=${encodeURIComponent(siteId)}`, signal);
  }

  /** Phase A of a library scan: one page of items, flat, at every folder depth. */
  getListItems(
    siteId: string,
    listId: string,
    skipToken: string | null,
    signal?: AbortSignal,
  ): Promise<ListItemsResponse> {
    const query = new URLSearchParams({ siteId, listId });
    if (skipToken !== null) {
      query.set('skipToken', skipToken);
    }

    return this.get<ListItemsResponse>(`/lists/items?${query.toString()}`, signal);
  }

  /** Phase B: the assignments on one item that stopped inheriting. */
  getItemPermissions(
    siteId: string,
    listId: string,
    itemId: number,
    /** The item's server-relative path, so the backend can find it in a drive without a lookup. */
    path: string | null,
    signal?: AbortSignal,
  ): Promise<ItemPermissionsResponse> {
    const query = new URLSearchParams({ siteId, listId, itemId: String(itemId) });
    if (path) {
      query.set('path', path);
    }

    return this.get<ItemPermissionsResponse>(`/lists/permissions?${query.toString()}`, signal);
  }

  searchPeople(query: string, signal?: AbortSignal): Promise<PeopleResponse> {
    return this.get<PeopleResponse>(`/people?q=${encodeURIComponent(query)}`, signal);
  }

  /**
   * Access granted at the site itself. The widest scope the tool reports: a
   * tenant-wide grant here reaches every library and item that inherits.
   */
  getSiteAccess(siteId: string, signal?: AbortSignal): Promise<SiteAccessResponse> {
    const query = new URLSearchParams({ siteId });
    return this.get<SiteAccessResponse>(`/sites/access?${query.toString()}`, signal);
  }

  /** Access granted on a library itself, which reaches everything inside that inherits. */
  getListAccess(
    siteId: string,
    listId: string,
    signal?: AbortSignal,
  ): Promise<ItemPermissionsResponse> {
    const query = new URLSearchParams({ siteId, listId });
    return this.get<ItemPermissionsResponse>(`/lists/access?${query.toString()}`, signal);
  }

  /** Direct members of a SharePoint group, fetched on click rather than during the scan. */
  getGroupMembers(siteId: string, groupId: number, signal?: AbortSignal): Promise<GroupMembersResponse> {
    const query = new URLSearchParams({ siteId, groupId: String(groupId) });
    return this.get<GroupMembersResponse>(`/groups/members?${query.toString()}`, signal);
  }

  /** Direct members of an Entra group. A different API from the SharePoint one, and the only one that works read-only. */
  getEntraGroupMembers(groupId: string, signal?: AbortSignal): Promise<GroupMembersResponse> {
    return this.get<GroupMembersResponse>(
      `/groups/entra-members?groupId=${encodeURIComponent(groupId)}`,
      signal,
    );
  }

  /**
   * Records an export. The data leaves the application at this point and this
   * is the last place it can be noted, so a failure here is swallowed rather
   * than blocking the download — but it is not silent in the console.
   */
  async recordExport(target: string, rowCount: number, destination: string): Promise<void> {
    const query = new URLSearchParams({
      target: `${target} (${rowCount} rows, ${destination})`,
    });

    try {
      // A POST, because it records something. It reaches the audit log and
      // nothing else; SharePoint is still read-only.
      await this.send('POST', `/audit/export?${query.toString()}`);
    } catch (error) {
      console.warn('The export could not be recorded in the audit log.', error);
    }
  }

  private get<T>(path: string, signal?: AbortSignal): Promise<T> {
    return this.send<T>('GET', path, signal);
  }

  private async send<T>(method: 'GET' | 'POST', path: string, signal?: AbortSignal): Promise<T> {
    const token = await this.session.getToken();

    const response = await fetch(`${this.config.apiBaseUrl}${path}`, {
      method,
      headers: { Authorization: `Bearer ${token}` },
      ...(signal ? { signal } : {}),
    });

    if (!response.ok) {
      throw await toApiError(response);
    }

    return (await response.json()) as T;
  }
}

async function toApiError(response: Response): Promise<ApiError> {
  let code = 'unknown';
  let message = `The request failed with status ${response.status}.`;
  let grantCommand: string | null = null;

  try {
    const body = (await response.json()) as {
      error?: string;
      message?: string;
      grantCommand?: string;
    };
    code = body.error ?? code;
    message = body.message ?? message;
    grantCommand = body.grantCommand ?? null;
  } catch {
    // A response with no JSON body still deserves an error the caller can act
    // on, so the status alone stands in.
  }

  const header = response.headers.get('Retry-After');
  const retryAfter = header !== null && /^\d+$/.test(header) ? Number(header) : null;

  const error = new ApiError(response.status, code, message, retryAfter);
  error.grantCommand = grantCommand;
  return error;
}
