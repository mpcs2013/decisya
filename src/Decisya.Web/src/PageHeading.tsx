import { useEffect, useRef, type ReactElement } from 'react';
import { NavigationType, useNavigationType } from 'react-router';
import { documentTitle } from './routes';

/** The page's h1: sets the document title and takes focus after a client-side navigation. */
export function PageHeading(props: { heading: string; focusOnMount?: boolean }): ReactElement {
  const { heading, focusOnMount = false } = props;
  const ref = useRef<HTMLHeadingElement>(null);
  const navigationType = useNavigationType();

  useEffect(() => {
    document.title = documentTitle(heading);
    // After a client-side navigation the h1 takes focus. The denial page takes it on a forced
    // navigation (a full load) too.
    if (focusOnMount || navigationType !== NavigationType.Pop) {
      ref.current?.focus();
    }
  }, [heading, focusOnMount, navigationType]);

  return (
    <h1 ref={ref} tabIndex={-1}>
      {heading}
    </h1>
  );
}
