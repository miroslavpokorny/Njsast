!function(t) {
    "use strict";
    var n, _, s, r, e, i, p, o;
    n = Object.setPrototypeOf || {
        __proto__: []
    } instanceof Array && function(t, n) {
        t.__proto__ = n;
    } || function(t, n) {
        var _;
        for (_ in n) if (n.hasOwnProperty(_)) t[_] = n[_];
    };
    _ = Object.assign || function(t) {
        var n, _, s, r;
        for (n = 1, _ = arguments.length; n < _; n++) {
            s = arguments[n];
            for (r in s) if (Object.prototype.hasOwnProperty.call(s, r)) t[r] = s[r];
        }
        return t;
    };
}();

