# Additional permission under GNU AGPL version 3 section 7

TotallyHot Arc Router is licensed under the GNU Affero General Public License, version 3
(see [`LICENSE`](LICENSE)). The following additional permission is granted under section 7 of that
license.

## Microsoft platform components

> As a special exception, the copyright holders of TotallyHot Arc Router give you permission to
> combine, link, or otherwise convey this program with the Microsoft Edge WebView2 Runtime, the
> Windows App SDK, and other Microsoft platform components distributed by Microsoft as part of, or
> as a supported runtime for, the Windows operating system, notwithstanding the terms under which
> Microsoft licenses those components. You may convey a combined work under the terms of the GNU
> Affero General Public License version 3 together with this exception. If you modify this program,
> you may extend this exception to your version, but you are not obliged to do so; if you do not
> wish to do so, delete this exception statement from your version.

### Why this exists

The Windows GUI (`TotallyHot.ArcRouter.Gui`) is a .NET MAUI Blazor Hybrid application. Its Razor UI
is rendered by a `BlazorWebView`, which requires the Microsoft Edge WebView2 Runtime — proprietary
software licensed by Microsoft, not by this project.

The AGPL's "System Libraries" definition (section 1) plausibly already covers WebView2, since it
ships as a component of Windows and of Microsoft Edge. But "plausibly" is not a good foundation for
a license posture. This explicit section 7 exception removes the ambiguity, so that neither this
project nor anyone redistributing it needs to litigate whether WebView2 counts as a System Library.

This exception is narrow by design. It permits linking against Microsoft's platform runtime; it does
**not** grant any permission to relicense this project's own source, and it does not extend to any
other proprietary component.

## What this exception does not change

- The copyleft terms of the AGPL still apply in full to TotallyHot Arc Router's own source code.
- Section 13 (the network-use / "SaaS" clause) still applies: if you run a modified version of this
  program and let users interact with it over a network, you must offer those users the
  corresponding source of your modified version.
- Third-party dependencies remain under their own licenses; see
  [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
