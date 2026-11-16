# Free / Pro and Gumroad licensing

This build is Free by default. `commerce.json` ships with an empty Gumroad product ID, so no checkout opens and no key can activate. Do not advertise Pro as released until both licensing and the Windows VM acceptance checklist pass.

| Feature | Free | Pro |
|---|---|---|
| Continuous collection, workload markers, history, CSV and reports | Yes | Yes |
| Explained sizing recommendations and reviewed manual changes | Yes | Yes |
| Backups, rollback, standalone recovery | Yes | Yes |
| Optional growth within approved limits | No | Yes |

Licensing moved from Lemon Squeezy to Gumroad in 1.2.1 so that Pagefile Monitor can be sold in the same store as Stele. No Lemon Squeezy store or licence ever existed, so nothing needs migrating.

## Publisher setup

1. In your Gumroad store, create the Pagefile Monitor Pro product. Decide the price yourself. On the product's Content page, add a **License key** module.
2. Copy the `product_id` shown in the License key module into `GumroadProductId` in the root `commerce.json`, and the product's `https://<store>.gumroad.com/l/<code>` address into `CheckoutUrl`. The checkout host must be `gumroad.com` or end in `.gumroad.com`. Both values are public identifiers, not secrets.
3. Run `build.ps1`; it copies the configuration into `release`. Keep all release files together. Never distribute a Gumroad access token or a customer license key with the app.
4. In an isolated Windows VM, install the service, make a test purchase, and activate the key through **License & About**. Verify activation, the three-activation limit, a refunded key, a key from another product, removal, and loss of network connectivity.
5. Complete the existing VM acceptance checklist for actual page-file changes before publishing the product.

## Customer experience and implementation

Get Pro opens the Gumroad product page; Gumroad emails the key with the receipt. Activation is entered into a masked administrator dialog after service installation. The key is never passed as a command-line argument or included in telemetry.

The license client posts `product_id`, `license_key` and `increment_uses_count` to `https://api.gumroad.com/v2/licenses/verify` over HTTPS. A key is accepted when Gumroad reports success for this product and the purchase is not refunded, charged back, under an unresolved dispute, or a lapsed subscription. Keys are stored using Windows machine-scope DPAPI in the protected service data directory. DPAPI is local credential protection, not protection from the PC's administrator or a guarantee against software patching. The source is public, so the check deters casual copying only.

**Activations.** One purchase covers one person on up to three activations. Activation checks the key without counting first, refuses a key that already has three activations, and only then counts one. Gumroad's public License API cannot give an activation back, so **Remove license from this PC** deletes the stored key locally and does not free an activation. A customer who reinstalls more than three times needs the publisher to reset the count in Gumroad.

**Re-checks and offline use.** The service re-checks a stored key about once a day without counting an activation. If Gumroad cannot be reached, Pro stays active and the check is retried hourly. Only an explicit answer from Gumroad turns Pro off: a timeout, a server error, a proxy page or a captive portal does not. A rejected key stays on disk and is retried hourly, so a publisher-side configuration mistake cannot erase customers' licenses. This matches Stele. The cost is that someone who blocks `api.gumroad.com` after activating keeps Pro after a refund.

Unlicensed installations cannot enable or execute auto-grow. Free functions, policy disabling, restoration and pending relocation recovery never depend on a license. A license check can take up to ten seconds on a network timeout; changes become visible after the next one-minute inventory cycle.

No memory or process telemetry is sent to Gumroad. Licensing transmits the product ID and the license key; checkout is handled in the browser. The buyer's email in Gumroad's reply is not read or stored.

For an existing installation: use **Settings → Update installed service** from the new complete release folder. An installed `commerce.json` is replaced only when the new one is configured. Existing old service binaries do not gain license enforcement merely because a new dashboard is opened.

## Evidence and remaining validation

Decision tests cover product identity, refund, chargeback, dispute, subscription state, the activation limit, the reachable/unreachable distinction and the checkout address. No request has been sent to Gumroad from this code: the product does not exist yet, so live checkout, activation, the 404 reply for an unknown key, and removal are untested. Decision tests are not an end-to-end billing validation.

Official reference: [Gumroad License API](https://gumroad.com/api#licenses).

## Inspiration

Inspired by Dave Plummer and [Task Manager TMOG](https://tmog.org/). The app uses an original navy/cyan instrument-panel theme. No TMOG code, artwork or fonts are bundled; no affiliation or endorsement is claimed.
