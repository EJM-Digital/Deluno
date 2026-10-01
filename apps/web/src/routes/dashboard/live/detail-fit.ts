/**
 * Fits each journey card's detail block to its height (#1098), as the mockup does: after the cards are placed, a card shows
 * as many whole lines as its height holds (the card keeps 8px under them, a line is 15px) and no more, so a short card
 * shows none and a tall one three, and nothing is ever cut through a line.
 */
import { detailRoom } from "./detail";

export function fitDetails(host: HTMLElement): void {
  for (const card of Array.from(host.querySelectorAll<HTMLElement>("[data-journey-card]"))) {
    const block = card.querySelector<HTMLElement>(".lj-x");
    if (!block) continue;
    const lines = Array.from(block.children) as HTMLElement[];
    block.style.display = "";
    for (const line of lines) line.style.display = "";
    const room = detailRoom(card.getBoundingClientRect().bottom, block.getBoundingClientRect().top);
    lines.forEach((line, index) => {
      line.style.display = index < room ? "" : "none";
    });
    // With no line to show the block is gone, margin and all, so a short card has nothing under its bar.
    block.style.display = room > 0 ? "" : "none";
  }
}
