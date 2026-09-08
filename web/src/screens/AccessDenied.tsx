import { LockIcon } from '../components/Icons';

interface Props {
  groupName: string;
  displayName: string;
}

/**
 * Sign-in succeeded but the token has no app role. Entra should normally have
 * refused the sign-in — appRoleAssignmentRequired is set on the service
 * principal — so reaching this screen means that outer layer is misconfigured
 * and the backend caught it instead.
 *
 * Never show an empty application here. A user with no findings and a user
 * with no access would look identical.
 */
export function AccessDenied({ groupName, displayName }: Props) {
  return (
    <main className="denied">
      <LockIcon className="denied-icon" />
      <h1>You don't have access to Permission insight</h1>
      <p>
        You're signed in as {displayName}, but your account isn't assigned the role this tool
        requires.
      </p>
      <p>
        Ask an administrator to add you to <code>{groupName}</code>. Access takes effect the next
        time you sign in.
      </p>
    </main>
  );
}
