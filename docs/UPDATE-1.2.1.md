# Pagefile Monitor 1.2.1 — November 16, 2026

A licensing release. Monitoring, recommendations, page-file changes, recovery and data formats are unchanged.

## What changed

- **Pro licensing uses Gumroad instead of Lemon Squeezy**, so the app can be sold beside Stele in one store. `commerce.json` now holds `GumroadProductId` and `CheckoutUrl`. They point at a Gumroad product that is an unpublished draft, so nothing is on sale and no customer keys exist.
- **Pro survives being offline.** Previously any failed check paused automatic growth. Now only an explicit rejection from Gumroad (refund, chargeback, dispute, wrong product, unknown key) turns Pro off. The key is re-checked about once a day instead of every minute.
- **Three activations per key**, matching Stele. Removing a license from a PC does not give an activation back, because Gumroad's public API has no call for that.
- The License & About text and the activation dialog name Gumroad.

A 1.2.0 `commerce.json` with zero Lemon Squeezy IDs reads as unconfigured in 1.2.1. See [LICENSING.md](LICENSING.md).

## Not validated

No key has been activated with this code, because the product is an unpublished draft with no purchases. Live checkout, activation, the activation limit, refund handling and offline behaviour all still need a test purchase in a VM. Everything listed as unvalidated in `UPDATE-1.1.1.md` is unchanged.
