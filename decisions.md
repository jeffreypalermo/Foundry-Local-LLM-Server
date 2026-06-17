# Decisions

## 2026-06-16

### Chat UI Refresh: Newest-on-Top + Explicit Dark Theme

**Date:** 2026-06-16T14:24:38-05:00  
**Author:** Trinity (Frontend Dev)  
**Status:** Accepted  
**Requested by:** Jeffrey Palermo

#### Context

The main web page (`frontend/src/App.tsx` + `App.css`) rendered the chat
transcript oldest-first and used pale pastel message cards over an OS-dependent
background (`color-scheme: light dark`), which produced weak, inconsistent
contrast and forced scrolling to find the latest reply.

#### Decision

1. **Newest exchange on top.** Group the flat `chat[]` (stored user-then-assistant)
   into `{ user, assistant }` exchange pairs with a pure render-time helper
   (`groupExchanges`), render the pairs, then reverse — so the most recent
   exchange is at the top while the User message stays directly above its own
   Assistant reply within each pair. State storage is unchanged; the view is
   derived in render. The in-flight case (latest user prompt, no reply yet)
   shows a dedicated "Generating response…" assistant bubble at the top while
   `busy`.

2. **Explicit, self-contained dark theme.** Define a cohesive token palette on
   `.app-shell` (slate-blue surfaces, indigo accent) instead of depending on the
   OS light/dark scheme. User = blue-tinted card with blue left border;
   Assistant = violet-tinted card with violet left border — clear visual
   distinction with WCAG AA body-text contrast. Buttons, textarea, config lines,
   and the error state get matching states (hover/focus/disabled/busy).

#### Rationale

- Newest-on-top matches user expectation for a running console and removes the
  need to scroll for the latest answer; grouping (not a flat reverse) keeps each
  prompt paired with its own response.
- An explicit theme guarantees readable, attractive contrast regardless of the
  viewer's OS appearance setting.

#### Constraints Respected

- Frontend-only; no backend/Aspire/test changes.
- All Playwright-protected selectors preserved: `button[type='submit']`
  ("Send Prompt"/"Running..."), `article.message.user`,
  `article.message.assistant p`, `p.error`, `p.config-line > strong` ("Model:").
- TypeScript strict; no implicit any. `npm run build` passes; `npx eslint src`
  clean. (Two pre-existing lint errors live only in `frontend/tests/*` spec
  files and are out of scope.)
