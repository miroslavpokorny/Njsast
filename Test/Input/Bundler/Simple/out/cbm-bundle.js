!function(t) {
    "use strict";
    var n, e, _, r, o, s;
    n = Object.setPrototypeOf || {
        __proto__: []
    } instanceof Array && function(t, n) {
        t.__proto__ = n;
    } || function(t, n) {
        var e;
        for (e in n) if (n.hasOwnProperty(e)) t[e] = n[e];
    };
    e = Object.assign || function(t) {
        var n, e, _, r;
        for (n = 1, e = arguments.length; n < e; n++) {
            _ = arguments[n];
            for (r in _) if (Object.prototype.hasOwnProperty.call(_, r)) t[r] = _[r];
        }
        return t;
    };
    _;
    r;
    o;
    s = !1;
    function i() {
        return "Hello";
    }
    console.log(i());
}();

