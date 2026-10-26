<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { PropType } from 'vue';
import { useRouter } from 'vue-router';
import { formatQuantity } from '@/utils/cart';

interface Props {
  title: string;
  count?: number;
}

const props = withDefaults(defineProps<Props>(), { count: 0 });
const emit = defineEmits<{ (event: 'select', id: number): void }>();
const isVisible = computed(() => props.count > 0);
const quantityLabel = computed(() => formatQuantity(props.count));
watch(() => props.count, count => emit('select', count));
const input = ref<HTMLInputElement | null>(null);
defineExpose({ focus: () => input.value?.focus() });
const router = useRouter();
</script>

<template>
  <section v-if="isVisible">
    <h2>{{ title }}</h2>
    <slot name="actions" />
  </section>
</template>
