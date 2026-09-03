export interface Order {
  readonly id: string;
  readonly sku: string;
  readonly quantity: number;
  readonly placedAt: string;
}

export interface OrderPage {
  readonly items: readonly Order[];
  readonly nextCursor: string | null;
}
