import { defineStore } from 'pinia';

interface CartItem {
  id: string;
  quantity: number;
}

export const useCartStore = defineStore('cart', {
  state: () => ({
    items: [] as CartItem[],
    loading: false,
  }),
  getters: {
    itemCount: (state) => state.items.length,
  },
  actions: {
    addItem(item: CartItem) {
      this.items.push(item);
    },
    clear() {
      this.items = [];
    },
  },
});
