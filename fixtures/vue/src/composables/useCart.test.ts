import { useCart } from '@/composables/useCart';

describe('useCart', () => {
  it('exposes cart state', () => {
    expect(useCart).toBeDefined();
  });
});
