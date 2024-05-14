<script>
  import { onMount } from 'svelte';
  import { fly, fade } from 'svelte/transition';
  let show = false;
  onMount(() => {
    show = true;
  });
</script>
{#if show}
  <div in:fly|global={{x: -300, duration: 600}} out:fly|global={{y: -50, duration: 200}}><slot></slot></div>
  <!-- <div in:fade|global={{delay: 1000, duration: 1000}} out:fade|global={{duration: 1000}}><slot></slot></div> -->
  <!-- <div><slot></slot></div> -->
{/if}
<style>
  div{
    position: absolute;
    width: 100%;
    height: 100%;
  }
</style>