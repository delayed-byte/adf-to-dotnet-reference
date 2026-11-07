# 0019. Where the KeyStone Db2 credentials live in Azure

Status: Proposed · Date: 2026-11-06

## Context

`adf/linkedService/LS_KeyStone_Db2.json` keeps only the password in Key Vault (`keystone-db2-extract-password`). The server (`keystone-db2-rpt.cu.internal:50001`), database (`KEYSTONE`), user (`ksextract`) and the TLS certificate name sit in the linked service as plain values.

The .NET code takes one setting, `KeyStoneDb2:ConnectionString` (`KeyStoneDb2Options`). It is used in two places:

- `Connections.CreateKeyStoneConnection` passes it to `DB2Connection`.
- At start-up, `Db2DriverConfiguration.DisableClientReroute` reads its `Server` and `Database` to write the `db2dsdriver.cfg` entry with `enableACR=false` (ADR 0009).

So the ADF secret cannot be used as it is. Locally the whole string is an environment variable built from `.env`. Nothing decides what to do in Azure.

The ADF linked service also connects over TLS (port 50001, `certificateCommonName`). The local Db2 container has no TLS, and the .NET code sets no TLS keywords. Whichever option is chosen, the Azure connection string must turn TLS on with the IBM provider's keywords (for example `Security=SSL`). **Not tested.**

## Options

| Option | What changes | For | Against |
| --- | --- | --- | --- |
| A. New secret with the whole connection string, e.g. `keystone-db2-extract-connection-string`, referenced from the `KeyStoneDb2__ConnectionString` app setting | Key Vault and app settings only; no code | Same shape as the SQL secret, which ADF already stores as a whole connection string (`reporting-sql-writer-connection-string`) | Host, port and TLS settings move into a secret, where reviewers don't see them. Rotating the password means rewriting the whole string. The existing rotation procedure for `keystone-db2-extract-password` changes. |
| B. Keep `keystone-db2-extract-password`. Add an optional `KeyStoneDb2:Password` setting (Key Vault reference) that the code puts into the connection string. | `KeyStoneDb2Options` gains `Password`. `Connections` adds it with `DB2ConnectionStringBuilder`. A unit test. `DisableClientReroute` is unaffected because it reads only `Server` and `Database`. | Keeps the ADF secret name and its rotation procedure. The non-secret part (host, port, TLS) stays a visible, reviewable app setting, as it is in the linked service. | A small code change. Two settings to keep consistent. The code must refuse a connection string that already has `PWD`, or say which value wins. |

## Proposed decision

**Option B.** It matches how the linked service separates the secret from the endpoint, and it keeps the DBA's rotation procedure. This is a recommendation. It needs the repository owner's approval and, for the rotation argument, confirmation from whoever rotates the Db2 password today.

If B is approved, the change is outside documentation. It touches `KeyStoneDb2Options`, `Connections`, a unit test, the README's `func start` section and the playbook's deployment checklist. Until then the local `func start` instructions stay as they are (option A shape, from `.env`).

## Consequences

- Choosing A needs no code. Record the new secret name in the playbook's deployment checklist.
- Either way, a password must never appear in a log. `Connections` does not log the connection string today. Any code that composes it must keep it that way.
- TLS for Db2 is an open deployment item for both options.
