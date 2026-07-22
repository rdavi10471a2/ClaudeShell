import { EventEmitter } from "node:events";
import type { SidecarEvent } from "./events.js";

export class EventBus {
  private bus = new EventEmitter();

  emit(event: SidecarEvent): void {
    this.bus.emit("event", event);
  }

  on(handler: (event: SidecarEvent) => void): void {
    this.bus.on("event", handler);
  }

  off(handler: (event: SidecarEvent) => void): void {
    this.bus.off("event", handler);
  }
}
