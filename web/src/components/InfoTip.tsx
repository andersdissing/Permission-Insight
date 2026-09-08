import { InfoIcon } from './Icons';

/**
 * A short explanation attached to a label, shown on hover.
 *
 * Distinct from the popovers elsewhere in the application, which open on click
 * and hold a paragraph or two. This is for a label that is accurate but not
 * self-explanatory — where the reader's question is "why?" and the answer is
 * one sentence.
 *
 * Focusable, and shown on keyboard focus as well as hover, because a hover
 * target that cannot be reached from the keyboard hides the explanation from
 * exactly the people most likely to need it. The text is also the accessible
 * name, so it is announced rather than described as "info".
 */
export function InfoTip({ text }: { text: string }) {
  return (
    <span className="info-tip" tabIndex={0} role="note" aria-label={text}>
      <InfoIcon />
      {/* Hidden from assistive technology: aria-label above already carries
          the same words, and announcing them twice is worse than once. */}
      <span className="info-tip-bubble" aria-hidden="true">
        {text}
      </span>
    </span>
  );
}
