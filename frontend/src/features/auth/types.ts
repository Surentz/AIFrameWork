/*
 * Hand-written, unlike features/orders/types.ts, which aliases the generated schema. There is no
 * auth endpoint in the backend yet, so nothing about this shape reaches openapi/AiFramework.Api.json
 * and there is nothing to alias. When the endpoint lands, these two interfaces become aliases over
 * the generated types the same way Order did, and the compiler starts catching backend renames here.
 */

export interface Credentials {
  readonly email: string;
  readonly password: string;
  readonly rememberMe: boolean;
}

export interface Session {
  readonly userId: string;
  readonly email: string;
  readonly displayName: string;
}
