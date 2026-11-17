# Static public site

Source for the requested GitHub Pages site. All demo objects are invented. No inventory, user paths, service credentials, benchmark records, telemetry, or live API integration belongs in this directory.

Build with `./eng/Build-Pages.ps1 -OutputDirectory artifacts/pages`. The build copies only this site's HTML/CSS/JS/SVG and the reusable spatial renderer from the product source; it does not copy the local service application or its data. Open the result through a local static server or publish it using the prepared Pages workflow.

The public source export substitutes README-public.md as its root README, so local campaign/evidence links do not leak into the public entry point. Source files remain available for inspection; no claim of remote publication is made by the build.

