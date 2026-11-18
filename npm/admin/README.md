# @winche/admin

A Winche project's data — documents, files, the app's users, assets, and calling functions — with one API wherever
it runs:

| Where | Acts as | Limited by |
| --- | --- | --- |
| Functions (`import … from "@winche/admin"`) | the server | nothing (access rules don't apply) |
| Console pages (the console resolves `@winche/admin`) | the person who opened the page | their role in the project |
| Anywhere else (`initializeAdmin`) | the token's account | its role in the project |

```ts
import { initializeAdmin, FieldValue } from "@winche/admin";

const admin = initializeAdmin({ server: "https://winche.example.com", project: "clinic", token: process.env.WINCHE_TOKEN! });
await admin.documents.doc("wards/a").update({ visits: FieldValue.increment(1) });
const report = await admin.files.child("reports/a.pdf").getData();
```

Roles: a viewer reads; a member also writes documents, files and the app's users; an admin may also call functions.

`as(user)` gives the same API acting as one of the app's users: documents and files calls are checked against the
project's access rules for them, as if the app had made them; `functions.call` passes them as the caller.

```ts
import { as } from "@winche/admin";
export const leave = callable(async (data, ctx) => {
  await as(ctx.caller!).documents.doc(`wards/${data.ward}/members/${ctx.caller!.uid}`).delete();   // the rules decide
});
```

Built from the SDK's one source in this repository (`src/Winche.Functions/Runtime/admin.ts`): `npm run build`
(Deno and Node), `npm test`.

## License

MIT (`LICENSE`). The SDK's source, `src/Winche.Functions/Runtime/admin.ts`, is MIT too; the rest of this repository
is under the Elastic License 2.0.
