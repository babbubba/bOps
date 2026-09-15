import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { StatusBadge } from '../../shared/status-badge';
import { TasksStore } from '../../state/tasks.store';

@Component({
  selector: 'bops-dashboard',
  imports: [FormsModule, StatusBadge],
  templateUrl: './dashboard.html',
})
export class Dashboard {
  protected readonly tasks = inject(TasksStore);
  protected readonly goal = signal('');

  protected async onStart(): Promise<void> {
    const goal = this.goal().trim();
    if (!goal) {
      return;
    }

    await this.tasks.start(goal);
    this.goal.set('');
  }

  protected select(taskId: string): void {
    this.tasks.selectTask(taskId);
  }

  protected async resume(taskId: string, event: Event): Promise<void> {
    event.stopPropagation();
    await this.tasks.resume(taskId);
  }

  protected shortGoal(goal: string): string {
    return goal.length > 60 ? `${goal.slice(0, 60)}…` : goal;
  }

  protected formatTime(iso: string): string {
    return new Date(iso).toLocaleTimeString();
  }
}
