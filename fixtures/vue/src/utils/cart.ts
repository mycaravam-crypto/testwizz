export function formatQuantity(quantity: number): string {
  return quantity === 1 ? '1 item' : `${quantity} items`;
}
