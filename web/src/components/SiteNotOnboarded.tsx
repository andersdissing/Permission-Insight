import { LockIcon } from './Icons';

interface Props {
  message: string;
  grantCommand: string | null;
}

/**
 * The site can be read but its permissions cannot, because nobody has granted
 * it to the tool yet.
 *
 * Presented as a missing step rather than an error. Tenant-wide access is
 * read-only by design, and reading permission data needs elevated access that
 * an administrator grants one site at a time. That is the point of the design,
 * not a defect in it, so this screen explains rather than apologises.
 *
 * The tool cannot grant itself the access. If it could, granting per site
 * would be worth nothing.
 */
export function SiteNotOnboarded({ message, grantCommand }: Props) {
  return (
    <div className="onboarding">
      <LockIcon className="onboarding-icon" />
      <h3>This site hasn't been granted yet</h3>
      <p>{message}</p>

      {grantCommand !== null && (
        <>
          <p className="muted">
            An administrator runs this once. Permission Insight can't grant itself access — that's
            what keeps a per-site grant meaningful.
          </p>
          <pre className="grant-command">{grantCommand}</pre>
        </>
      )}
    </div>
  );
}
