# Public announcements — R-api-19

The News page reads the official DevForum announcements RSS feed only when opened or refreshed. It retains at most 30 plain-text articles in memory, makes one anonymous GET with Zenith's own User-Agent, disables cookies/credentials/redirects, and bounds the response to 256 KiB and 15 seconds. RSS 2 and Atom are supported; XML entities/DTDs and HTML pages are rejected. Embedded markup is stripped, images and remote resources are never fetched, and only exact official HTTPS article hosts/paths without userinfo, custom ports, query or fragment can be presented for opening. An article opens through an explicit default-No browser confirmation, followed by a fresh current-page/item validation.

Navigation away, Panic, Lock and shutdown cancel the single page-owned request and fence late results. Disposal joins the actual request, including repeated disposal callers. Sample mode and locked state send nothing. There is no timer, background polling, HTML scraping or account/session access. Existing themes and page layout are preserved; the addition is one sidebar entry and a plain article list using existing resources.

Anonymous header-only probes on November 8, 2026 returned HTTP 200 `application/rss+xml` for `https://devforum.roblox.com/c/updates/announcements/36.rss`. The four Newsroom candidates (`https://about.roblox.com/newsroom/rss`, `/newsroom/feed`, `/rss.xml`, `/feed`) returned HTTP 404 HTML. They are not configured or fetched by the app. **Owner question pending:** use DevForum announcements plus an external Newsroom button until a valid official RSS/Atom URL is available, or supply/wait for that URL? The currently built page clearly says no Newsroom feed is configured. No second feed is claimed complete.

Evidence under `artifacts/completion-integration/`:

- `news-before.trx`: 16 contract tests failed against the unavailable-reader baseline.
- `news-after.trx`: all 16 parser/request tests passed.
- `news-ui.trx`: 29 parser, page-owner and localization cases passed.
- `news-disposal-before.trx`: a review regression proved repeated disposal falsely completed before the first owner retired.
- `news-reviewed.trx`: 38 checks passed, zero failures/skips, including the disposal repair, real shell navigation/Panic/Lock wiring and the seven merged public capture checks.

All parser tests use fake HTTP responses; the shell test uses a synthetic monitor and temporary data. Public endpoint existence was observed anonymously; native sessions, private data, deployment readiness and the final full-suite gate are not established by these checks.
