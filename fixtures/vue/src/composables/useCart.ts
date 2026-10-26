import { computed } from 'vue';
import { useCartStore } from '@/stores/cart';

export function useCart() {
  const store = useCartStore();
  const itemCount = computed(() => store.items.length);
  return { store, itemCount };
}
