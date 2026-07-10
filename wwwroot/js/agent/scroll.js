AgentThreadManager.prototype._scrollToBottom = function() {
        if (!this.autoScrollPinned) {
            return;
        }

        this.thread.scrollTop = this.thread.scrollHeight;
        this.autoScrollPinned = true;
};

AgentThreadManager.prototype._isNearBottom = function() {
        const distance = this.thread.scrollHeight - this.thread.scrollTop - this.thread.clientHeight;
        return distance <= 48;
};
