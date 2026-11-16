# Free / Pro and Lemon Squeezy setup

This build is Free by default. No live store, product, price or checkout has been created. Do not advertise Pro as released until both licensing and the Windows VM acceptance checklist pass.

| Feature | Free | Pro |
|---|---|---|
| Continuous collection, workload markers, history, CSV and reports | Yes | Yes |
| Explained sizing recommendations and reviewed manual changes | Yes | Yes |
| Backups, rollback, standalone recovery | Yes | Yes |
| Optional growth within approved limits | No | Yes |

## Publisher setup

1. Create your Lemon Squeezy account and store. Complete its required seller onboarding yourself.
2. In test mode, create a Pagefile Monitor Pro product/variant. Decide the price and whether it is a single payment or subscription before publishing. Enable **Generate License Keys**, and select an activation limit and license duration appropriate to the offer. These commercial choices are intentionally not invented in this build.
3. Put the store, product and Pro variant IDs and the hosted HTTPS checkout URL into the root `commerce.json`. All IDs must be positive. The checkout host must end in `.lemonsqueezy.com`. These are public identifiers, not API secrets.
4. Run `build.ps1`; it copies the configuration into `release`. Keep all release files together. Never distribute a merchant API key or customer license key with the app.
5. In an isolated Windows VM, install the service, complete a test purchase, and activate the resulting test key through **License & About**. Verify activation, instance limits, deactivation, expiry/disabled keys, wrong-product rejection and loss of network connectivity. Test identifiers and live identifiers must be configured separately; do not distribute a production build pointed at test products.
6. Complete the existing VM acceptance checklist for actual page-file changes. Configure live product IDs and checkout only after store approval and successful testing. Rebuild and distribute the approved release.

## Customer experience and implementation

Get Pro opens hosted checkout; Lemon Squeezy delivers the key. Activation is entered into a masked administrator dialog after service installation. The key is never passed as a command-line argument or included in telemetry. A random activation label is used rather than a hardware serial number or computer name.

The license client uses HTTPS form requests to the public License API. It validates store/product/variant identity, active status, expiry and instance ID. Keys are stored using Windows machine-scope DPAPI in the protected service data directory. DPAPI is local credential protection, not protection from the PC's administrator or a guarantee against software patching.

The service independently verifies the license during each minute inventory cycle, outside the sampling lock. Unverified licenses cannot enable or execute auto-grow. No offline Pro grace period is provided in this version. A network failure pauses automated growth; Free functions, policy disabling, restoration and pending relocation recovery remain usable. Validation can take up to ten seconds on a network timeout; changes become visible after the next successful inventory cycle. Revalidation succeeding resumes an already enabled growth policy under all existing safety gates.

No memory or process telemetry is sent to Lemon Squeezy. Licensing transmits the license key and activation identifier; checkout is handled in the browser. No license response or customer name/email is stored in telemetry.

For an existing installation: remove the service through the old dashboard, then install from the new complete release folder. This retains telemetry/backups and does not itself change page-file settings. Do not perform this during a pending configuration operation. Existing old service binaries do not gain license enforcement merely because a new dashboard is opened.

## Evidence and remaining validation

Release build and 34 automated tests pass, including product identity, instance binding, expiry, revocation and unconfigured-Free decisions. Overview and License & About were visually inspected. Live checkout/activation/deactivation, installation of this new service and privileged Windows changes have not been tested. The account/product does not yet exist. Decision tests are not an end-to-end billing validation.

Official references: [License API](https://docs.lemonsqueezy.com/api/license-api), [activation and identity checks](https://docs.lemonsqueezy.com/guides/tutorials/license-keys), [generating license keys](https://docs.lemonsqueezy.com/help/licensing/generating-license-keys), [test mode](https://docs.lemonsqueezy.com/help/getting-started/test-mode).

## Inspiration

Inspired by Dave Plummer and [Task Manager TMOG](https://tmog.org/). The app uses an original navy/cyan instrument-panel theme. No TMOG code, artwork or fonts are bundled; no affiliation or endorsement is claimed.
