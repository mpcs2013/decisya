import { Fragment, useLayoutEffect, type ReactElement, type ReactNode } from 'react';
import { Link } from 'react-router';
import { PageHeading } from '../PageHeading';
import type { Block, Inline, LegalSection, LegalText, Passage } from './model';
import { markerAttributes } from './marker';
import { legalHref, type LegalLanguage } from './pages';
import { SupportEmailLink } from './SupportEmailLink';
import { PLACEHOLDER_ADDRESS_WORDS, PLACEHOLDER_WORDS, VERSION_WORD } from './texts/markers';
import { LEGAL_VERSIONS } from './texts/versions';

export type LanguageScope = 'document' | 'element';

function InlineView(props: { part: Inline; language: LegalLanguage }): ReactElement {
  const { part, language } = props;
  if (typeof part === 'string') {
    return <>{part}</>;
  }
  if (part.kind === 'legal-link') {
    return <Link to={legalHref(part.page, language)}>{part.label}</Link>;
  }
  return <SupportEmailLink markerWords={PLACEHOLDER_ADDRESS_WORDS[language]} />;
}

function InlineList(props: { parts: readonly Inline[]; language: LegalLanguage }): ReactElement {
  return (
    <>
      {props.parts.map((part, index) => (
        <Fragment key={index}>
          <InlineView part={part} language={props.language} />
        </Fragment>
      ))}
    </>
  );
}

function BlockView(props: { block: Block; language: LegalLanguage }): ReactElement {
  const { block, language } = props;
  if (block.kind === 'paragraph') {
    return (
      <p>
        <InlineList parts={block.content} language={language} />
      </p>
    );
  }
  return (
    <ul>
      {block.items.map((item, index) => (
        <li key={index}>
          <InlineList parts={item} language={language} />
        </li>
      ))}
    </ul>
  );
}

function PassageView(props: {
  passage: Passage;
  language: LegalLanguage;
  headingTag: 'h3' | 'h4';
}): ReactElement {
  const { passage, language, headingTag: Heading } = props;
  const body = (
    <>
      {passage.heading === undefined ? null : <Heading>{passage.heading}</Heading>}
      {passage.blocks.map((block, index) => (
        <BlockView key={index} block={block} language={language} />
      ))}
      {passage.placeholder === undefined ? null : (
        <p className="placeholder-words">{PLACEHOLDER_WORDS[language]}</p>
      )}
    </>
  );
  if (passage.placeholder === undefined) {
    return <div className="legal-passage">{body}</div>;
  }
  return (
    <div className="legal-passage" {...markerAttributes(passage.id, passage.placeholder)}>
      {body}
    </div>
  );
}

function SectionView(props: {
  section: LegalSection;
  language: LegalLanguage;
  scope: LanguageScope;
}): ReactElement {
  const { section, language, scope } = props;
  const SectionHeading = scope === 'document' ? 'h2' : 'h3';
  return (
    <section aria-labelledby={`legal-${section.id}`}>
      <SectionHeading id={`legal-${section.id}`}>{section.heading}</SectionHeading>
      {section.passages.map((passage) => (
        <PassageView
          key={passage.id}
          passage={passage}
          language={language}
          headingTag={scope === 'document' ? 'h3' : 'h4'}
        />
      ))}
    </section>
  );
}

function VersionLabel(props: { text: LegalText }): ReactElement {
  const { page, language } = props.text;
  const version = LEGAL_VERSIONS[page];
  const content = (
    <>
      {VERSION_WORD[language]} {version.label}
      {version.placeholder === undefined ? null : (
        <>
          {' '}
          <span className="placeholder-words">{PLACEHOLDER_WORDS[language]}</span>
        </>
      )}
    </>
  );
  if (version.placeholder === undefined) {
    return <p className="legal-version">{content}</p>;
  }
  return (
    <p className="legal-version" {...markerAttributes('version', version.placeholder)}>
      {content}
    </p>
  );
}

/**
 * Renders one legal text as React elements. With scope 'document' (a legal page) the document
 * language and title follow the text and the title is the page's h1. With 'element' (an embedded
 * text) the document is not touched: a wrapper carries the language and the headings start one
 * level lower. `intro` is shown after the title and version, before the text (the language
 * choice on a legal page).
 */
export function LegalTextView(props: {
  text: LegalText;
  languageScope: LanguageScope;
  intro?: ReactNode;
}): ReactElement {
  const { text, languageScope, intro } = props;

  useLayoutEffect(() => {
    if (languageScope !== 'document') {
      return undefined;
    }
    document.documentElement.lang = text.language;
    return () => {
      document.documentElement.lang = 'en';
    };
  }, [languageScope, text.language]);

  const content = (
    <>
      {languageScope === 'document' ? (
        <PageHeading heading={text.title} />
      ) : (
        <h2>{text.title}</h2>
      )}
      <VersionLabel text={text} />
      {intro}
      {text.sections.map((section) => (
        <SectionView
          key={section.id}
          section={section}
          language={text.language}
          scope={languageScope}
        />
      ))}
    </>
  );

  if (languageScope === 'element') {
    return <div lang={text.language}>{content}</div>;
  }
  return <article className="legal-text">{content}</article>;
}
